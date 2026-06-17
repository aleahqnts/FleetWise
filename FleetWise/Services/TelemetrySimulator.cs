using System.Text.Json;
using System.Text.Json.Serialization;
using FleetWise.Models;

namespace FleetWise.Services;

/// <summary>
/// Development-only stand-in producer for live telemetry. Every 5 seconds it advances
/// each Active trip along its route geometry and inserts one row into the Supabase
/// <c>telemetry_data</c> table — the same table real hardware would write to, so the
/// read path never depends on the data being simulated.
/// </summary>
public class TelemetrySimulator : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    // Bus-like speed envelope (km/h) — jittered per tick so motion looks organic.
    private const double MinSpeedKmh = 20.0;
    private const double MaxSpeedKmh = 40.0;

    // Per-tick passenger drift bounds (clamped to [0, vehicle capacity]).
    private const int MaxPassengerDelta = 3;

    private readonly Supabase.Client _supabase;
    private readonly ILogger<TelemetrySimulator> _logger;
    private readonly Random _rng = new();

    // Route geometry never changes mid-run, so it's cached after the first read.
    private readonly Dictionary<int, RouteGeometry> _geometryCache = new();

    // Per-trip simulation state lives in memory; on restart buses resume from the route start.
    private readonly Dictionary<string, TripState> _states = new();

    // A driver to attach to auto-created trips (driver_id is NOT NULL); resolved once.
    private int? _cachedDriverId;

    public TelemetrySimulator(Supabase.Client supabase, ILogger<TelemetrySimulator> logger)
    {
        _supabase = supabase;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The Supabase.Client is a singleton used directly here; there are no scoped
        // services to resolve per tick, so no DI scope is created.
        using var timer = new PeriodicTimer(TickInterval);

        while (!stoppingToken.IsCancellationRequested &&
               await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A transient Supabase hiccup must not kill the loop — log and try again next tick.
                _logger.LogWarning(ex, "TelemetrySimulator tick failed; will retry next interval.");
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // Keep the registry and the map in agreement: a vehicle marked 'On Trip' should
        // actually be running. Auto-create an Active trip for any On-Trip vehicle that
        // lacks one, so every On-Trip bus moves.
        await EnsureActiveTripsForOnTripVehiclesAsync();

        var tripsResponse = await _supabase
            .From<Trip>()
            .Filter("trip_status", Postgrest.Constants.Operator.Equals, "Active")
            .Get();

        var activeTrips = tripsResponse.Models;
        if (activeTrips.Count == 0)
            return;

        // Capacities for clamping passenger drift — one small read, fixed vehicle set.
        var vehiclesResponse = await _supabase.From<Vehicle>().Get();
        var capacityByVehicle = vehiclesResponse.Models
            .GroupBy(v => v.VehicleId)
            .ToDictionary(g => g.Key, g => g.First().Capacity);

        foreach (var trip in activeTrips)
        {
            var geometry = await GetGeometryAsync(trip.RouteId);
            if (geometry is null)
                continue; // route has no usable waypoints — nothing to animate

            var capacity = capacityByVehicle.TryGetValue(trip.VehicleId ?? string.Empty, out var c)
                ? c
                : 50;

            var state = AdvanceTrip(trip.TripId, geometry, capacity, trip.TotalBoarded);

            var telemetry = new TelemetryData
            {
                TripId = trip.TripId,
                Latitude = (decimal)state.Lat,
                Longitude = (decimal)state.Lng,
                CurrentPassengers = state.Passengers,
                Speed = Math.Round((decimal)state.SpeedKmh, 1),
                Heading = (float)state.Heading,
                Timestamp = PhClock.Now
            };

            await _supabase.From<TelemetryData>().Insert(telemetry);

            // Persist the cumulative boardings so the map's revenue (total_boarded × fare)
            // grows and never drops when passengers alight. Column-targeted update — only
            // total_boarded is written, leaving the trip's date and other fields untouched.
            if (state.TotalBoarded != trip.TotalBoarded)
            {
                await _supabase.From<Trip>()
                    .Where(t => t.TripId == trip.TripId)
                    .Set(t => t.TotalBoarded, state.TotalBoarded)
                    .Update();
            }
        }
    }

    /// <summary>
    /// Reconcile vehicle_status with the trips table: every 'On Trip' vehicle that has a
    /// route but no Active trip gets a fresh Active trip, so the Vehicles registry's On-Trip
    /// count and the Fleet Map's moving buses always agree. Idempotent — once a trip exists
    /// the vehicle is skipped on later ticks.
    /// </summary>
    private async Task EnsureActiveTripsForOnTripVehiclesAsync()
    {
        var vehicles = (await _supabase.From<Vehicle>().Get()).Models;
        var onTrip = vehicles
            .Where(v => string.Equals(v.VehicleStatus?.Trim(), "On Trip", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (onTrip.Count == 0)
            return;

        var activeTrips = (await _supabase
            .From<Trip>()
            .Filter("trip_status", Postgrest.Constants.Operator.Equals, "Active")
            .Get()).Models;

        var vehiclesWithActiveTrip = activeTrips
            .Where(t => t.VehicleId != null)
            .Select(t => t.VehicleId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A vehicle needs a route (with geometry) to be animated; skip routeless ones.
        var missing = onTrip
            .Where(v => v.RouteId.HasValue && !vehiclesWithActiveTrip.Contains(v.VehicleId))
            .ToList();
        if (missing.Count == 0)
            return;

        var driverId = await GetAnyDriverIdAsync();
        if (driverId is null)
        {
            _logger.LogWarning("No driver found to attach auto-created trips; skipping reconcile.");
            return;
        }

        foreach (var v in missing)
        {
            // trip_id is auto-generated by the DB (sequence default) — do NOT supply it.
            var trip = new Trip
            {
                Date = PhClock.Today,
                ShiftType = "Morning",
                ShiftStartTime = new TimeSpan(6, 0, 0),
                ShiftEndTime = new TimeSpan(14, 0, 0),
                RouteId = v.RouteId!.Value,
                VehicleId = v.VehicleId,
                DriverId = driverId.Value,
                TripStatus = "Active",
                EstimatedRevenue = 0,
            };

            await _supabase.From<Trip>().Insert(trip);
            _logger.LogInformation("Auto-created an Active trip for On-Trip vehicle {VehicleId}.", v.VehicleId);
        }
    }

    private async Task<int?> GetAnyDriverIdAsync()
    {
        if (_cachedDriverId is int cached)
            return cached;

        // Drivers are role_id = 2 (see seeded roles); fall back to any user if none.
        var drivers = (await _supabase
            .From<UserModel>()
            .Filter("role_id", Postgrest.Constants.Operator.Equals, "2")
            .Get()).Models;

        var driver = drivers.FirstOrDefault()
                     ?? (await _supabase.From<UserModel>().Get()).Models.FirstOrDefault();

        _cachedDriverId = driver?.UserId;
        return _cachedDriverId;
    }

    /// <summary>Advance one trip along its route by one tick and return its new state.</summary>
    private TripState AdvanceTrip(string tripId, RouteGeometry geometry, int capacity, int dbTotalBoarded)
    {
        if (!_states.TryGetValue(tripId, out var state))
        {
            // First sighting: start somewhere along the route with a plausible load. Seed
            // cumulative boardings from the DB (so revenue survives a restart) but never
            // below the current load — everyone aboard boarded at some point.
            var initialPassengers = _rng.Next(0, Math.Max(1, (int)(capacity * 0.6)));
            state = new TripState
            {
                DistanceMeters = _rng.NextDouble() * geometry.TotalLength,
                Passengers = initialPassengers,
                TotalBoarded = Math.Max(dbTotalBoarded, initialPassengers)
            };
            _states[tripId] = state;
        }

        // Move forward by speed × interval, looping at the end of the polyline.
        var speedKmh = MinSpeedKmh + _rng.NextDouble() * (MaxSpeedKmh - MinSpeedKmh);
        var metresPerTick = speedKmh / 3.6 * TickInterval.TotalSeconds;
        state.DistanceMeters = (state.DistanceMeters + metresPerTick) % geometry.TotalLength;
        state.SpeedKmh = speedKmh;

        var (lat, lng, heading) = geometry.LocateAt(state.DistanceMeters);
        state.Lat = lat;
        state.Lng = lng;
        state.Heading = heading;

        // Drift passengers by a small random delta, clamped to the vehicle's capacity.
        var delta = _rng.Next(-MaxPassengerDelta, MaxPassengerDelta + 1);
        var newPassengers = Math.Clamp(state.Passengers + delta, 0, capacity);

        // Count only boardings (positive change) toward the cumulative total.
        var boarded = newPassengers - state.Passengers;
        if (boarded > 0)
            state.TotalBoarded += boarded;

        state.Passengers = newPassengers;

        return state;
    }

    private async Task<RouteGeometry?> GetGeometryAsync(int routeId)
    {
        if (_geometryCache.TryGetValue(routeId, out var cached))
            return cached;

        var response = await _supabase
            .From<BusRoute>()
            .Filter("route_id", Postgrest.Constants.Operator.Equals, routeId)
            .Get();

        var route = response.Models.FirstOrDefault();
        var geometry = RouteGeometry.FromJson(route?.WaypointsJson);

        // Cache even a null result so we don't re-query a geometry-less route every tick.
        _geometryCache[routeId] = geometry!;
        return geometry;
    }

    private sealed class TripState
    {
        public double DistanceMeters { get; set; }
        public double Lat { get; set; }
        public double Lng { get; set; }
        public double Heading { get; set; }
        public double SpeedKmh { get; set; }
        public int Passengers { get; set; }
        public int TotalBoarded { get; set; }
    }

    /// <summary>An ordered polyline with cumulative segment distances for interpolation.</summary>
    private sealed class RouteGeometry
    {
        private readonly List<(double Lat, double Lng)> _points;
        private readonly List<double> _cumulative; // metres from start at each point
        public double TotalLength { get; }

        private RouteGeometry(List<(double Lat, double Lng)> points, List<double> cumulative)
        {
            _points = points;
            _cumulative = cumulative;
            TotalLength = cumulative[^1];
        }

        public static RouteGeometry? FromJson(string? waypointsJson)
        {
            if (string.IsNullOrWhiteSpace(waypointsJson))
                return null;

            List<WaypointDto>? raw;
            try
            {
                raw = JsonSerializer.Deserialize<List<WaypointDto>>(waypointsJson);
            }
            catch (JsonException)
            {
                return null;
            }

            if (raw is null || raw.Count < 2)
                return null;

            var points = raw.Select(w => (w.Lat, w.Lng)).ToList();

            var cumulative = new List<double> { 0 };
            for (var i = 1; i < points.Count; i++)
            {
                var segment = Haversine(points[i - 1], points[i]);
                cumulative.Add(cumulative[i - 1] + segment);
            }

            // A degenerate (zero-length) route can't be animated.
            return cumulative[^1] > 0 ? new RouteGeometry(points, cumulative) : null;
        }

        /// <summary>Interpolate position and travel heading at a distance along the route.</summary>
        public (double Lat, double Lng, double Heading) LocateAt(double distanceMeters)
        {
            var d = Math.Clamp(distanceMeters, 0, TotalLength);

            // Find the segment [i, i+1] containing d.
            var i = 0;
            while (i < _cumulative.Count - 2 && _cumulative[i + 1] < d)
                i++;

            var segStart = _cumulative[i];
            var segLength = _cumulative[i + 1] - segStart;
            var t = segLength > 0 ? (d - segStart) / segLength : 0;

            var (lat1, lng1) = _points[i];
            var (lat2, lng2) = _points[i + 1];

            var lat = lat1 + (lat2 - lat1) * t;
            var lng = lng1 + (lng2 - lng1) * t;
            var heading = Bearing((lat1, lng1), (lat2, lng2));

            return (lat, lng, heading);
        }

        private static double Haversine((double Lat, double Lng) a, (double Lat, double Lng) b)
        {
            const double earthRadius = 6_371_000; // metres
            var dLat = ToRad(b.Lat - a.Lat);
            var dLng = ToRad(b.Lng - a.Lng);
            var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos(ToRad(a.Lat)) * Math.Cos(ToRad(b.Lat)) *
                    Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
            return earthRadius * 2 * Math.Atan2(Math.Sqrt(h), Math.Sqrt(1 - h));
        }

        private static double Bearing((double Lat, double Lng) a, (double Lat, double Lng) b)
        {
            var lat1 = ToRad(a.Lat);
            var lat2 = ToRad(b.Lat);
            var dLng = ToRad(b.Lng - a.Lng);
            var y = Math.Sin(dLng) * Math.Cos(lat2);
            var x = Math.Cos(lat1) * Math.Sin(lat2) -
                    Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLng);
            return (ToDeg(Math.Atan2(y, x)) + 360) % 360;
        }

        private static double ToRad(double deg) => deg * Math.PI / 180.0;
        private static double ToDeg(double rad) => rad * 180.0 / Math.PI;
    }

    private sealed class WaypointDto
    {
        [JsonPropertyName("lat")]
        public double Lat { get; set; }

        [JsonPropertyName("lng")]
        public double Lng { get; set; }
    }
}
