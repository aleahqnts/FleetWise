using System.Text.Json;
using System.Text.Json.Serialization;
using FleetWise.Models;

namespace FleetWise.Services;

/// <summary>
/// Stand-in producer for live telemetry, for use in development. Every five seconds it
/// advances each active trip along its route and writes to the same telemetry table real
/// hardware writes to, so nothing downstream depends on the data being simulated.
/// </summary>
public class TelemetrySimulator : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    // Speed range in kilometres per hour, varied slightly each tick so movement does not
    // look mechanical.
    private const double MinSpeedKmh = 20.0;
    private const double MaxSpeedKmh = 40.0;

    // Bounds for passenger changes, clamped to the vehicle's capacity. Applied on a
    // realistic cadence rather than every tick, so boardings, and therefore the revenue
    // figures derived from them, accumulate at a believable rate over a day.
    private const int MaxPassengerDelta = 3;
    private static readonly TimeSpan PassengerDriftInterval = TimeSpan.FromSeconds(30);

    // Write throttle, matching the phone's own rule: a tick writes a row only when the bus
    // has moved far enough, the passenger count changed, or the heartbeat interval elapsed.
    // Positions still advance every tick in memory, so only the write is gated and the
    // table does not gain a row per bus every five seconds.
    private const double MinWriteMeters = 25.0;
    private static readonly TimeSpan WriteHeartbeat = TimeSpan.FromSeconds(60);

    private readonly Supabase.Client _supabase;
    private readonly ILogger<TelemetrySimulator> _logger;
    private readonly SimulatorControl _control;
    private readonly Random _rng = new();

    // Route geometry does not change while running, so it is cached after the first read.
    private readonly Dictionary<int, RouteGeometry> _geometryCache = new();

    // Per-trip state is held in memory, so after a restart buses resume from the start of
    // their route.
    private readonly Dictionary<string, TripState> _states = new();

    // A driver for automatically created trips, since the column cannot be null. Resolved once.
    private int? _cachedDriverId;

    public TelemetrySimulator(Supabase.Client supabase, ILogger<TelemetrySimulator> logger, SimulatorControl control)
    {
        _supabase = supabase;
        _logger = logger;
        _control = control;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The database client is a singleton used directly, and there are no scoped
        // services to resolve, so no scope is created per tick.
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
                // A transient failure must not end the loop, so it is logged and retried.
                _logger.LogWarning(ex, "TelemetrySimulator tick failed; will retry next interval.");
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // Checked before taking the gate, to avoid contending with a stop and its cleanup
        // when the simulator is already off.
        if (!_control.Enabled)
            return;

        // The gate is held for the whole tick so a stop and its cleanup cannot run
        // alongside it. The switch is re-checked once the gate is held: if a stop completed
        // in the meantime, this tick must not create a trip and resurrect demo data while
        // the simulator is off.
        await _control.TickGate.WaitAsync(ct);
        try
        {
            if (!_control.Enabled)
                return;

            await TickBodyAsync(ct);
        }
        finally
        {
            _control.TickGate.Release();
        }
    }

    private async Task TickBodyAsync(CancellationToken ct)
    {
        // Any demo trip left from an earlier operational day is closed, so boardings and
        // the revenue derived from them reset each cycle rather than growing without bound.
        // This runs before the spawn below, so the trip that replaces it is dated today.
        await RollOverStaleDemoTripsAsync();

        // One demo trip per route, so turning the simulator on always produces moving
        // buses regardless of any vehicle's stored status.
        await EnsureDemoTripsAsync();

        var tripsResponse = await _supabase
            .From<Trip>()
            .Filter("trip_status", Postgrest.Constants.Operator.Equals, "Active")
            .Get();

        // Only the simulator's own trips are animated. Real driver trips are never touched,
        // so live positions and passenger counts cannot be overwritten.
        var activeTrips = tripsResponse.Models
            .Where(t => t.IsSimulated)
            .ToList();
        if (activeTrips.Count == 0)
            return;

        // Capacities used to clamp passenger changes. One small read over a fixed set.
        var vehiclesResponse = await _supabase.From<Vehicle>().Get();
        var capacityByVehicle = vehiclesResponse.Models
            .GroupBy(v => v.VehicleId)
            .ToDictionary(g => g.Key, g => g.First().Capacity);

        foreach (var trip in activeTrips)
        {
            var geometry = await GetGeometryAsync(trip.RouteId);
            if (geometry is null)
                continue; // no usable waypoints on this route, so nothing to animate

            var capacity = capacityByVehicle.TryGetValue(trip.VehicleId ?? string.Empty, out var c)
                ? c
                : 50;

            var state = AdvanceTrip(trip.TripId, geometry, capacity, trip.TotalBoarded);

            if (ShouldWrite(state))
            {
                var telemetry = new TelemetryData
                {
                    TripId = trip.TripId,
                    Latitude = (decimal)state.Lat,
                    Longitude = (decimal)state.Lng,
                    TotalPassengers = state.Passengers,
                    Speed = Math.Round((decimal)state.SpeedKmh, 1),
                    Heading = (float)state.Heading,
                    Timestamp = PhClock.Now
                };

                await _supabase.From<TelemetryData>().Insert(telemetry);

                state.LastWriteUtc = DateTime.UtcNow;
                state.LastWrittenLat = state.Lat;
                state.LastWrittenLng = state.Lng;
                state.LastWrittenPassengers = state.Passengers;
                state.HasWritten = true;
            }

            // Cumulative boardings are stored, so revenue rises and never falls when
            // passengers alight. Only that column is written, leaving the trip's date and
            // other fields alone.
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
    /// Closes simulated trips left over from an earlier operational day.
    ///
    /// Demo trips run indefinitely, so a single trip would accumulate boardings across days
    /// and inflate revenue without bound. Finalizing at the cycle boundary lets a fresh trip
    /// start dated today, with boardings back to a realistic daily figure. Real driver trips
    /// are never touched.
    /// </summary>
    private async Task RollOverStaleDemoTripsAsync()
    {
        var opDay = PhClock.OperationalDay.Date;

        var staleTrips = (await _supabase
            .From<Trip>()
            .Filter("trip_status", Postgrest.Constants.Operator.Equals, "Active")
            .Get()).Models
            .Where(t => t.IsSimulated && t.Date.Date < opDay)
            .ToList();

        foreach (var trip in staleTrips)
        {
            // Finalized under its own earlier date, so that day's history keeps its
            // totals. The route is then without a demo trip, and the next tick creates one.
            await _supabase.From<Trip>()
                .Where(t => t.TripId == trip.TripId)
                .Set(t => t.TripStatus, "Completed")
                .Set(t => t.ActualEndTime, PhClock.Now)
                .Update();

            _states.Remove(trip.TripId); // drop its in-memory animation/boarding state
            _logger.LogInformation(
                "Rolled over demo trip {TripId} (dated {Date:yyyy-MM-dd}) at the operational-day boundary.",
                trip.TripId, trip.Date);
        }
    }

    /// <summary>
    /// Ensures one demo trip exists per route that has geometry, so turning the simulator
    /// on always produces moving buses regardless of any vehicle's stored status.
    ///
    /// Chooses a deployable bus on the route: not out of service, and not already on an
    /// active trip. Idempotent, so a route that already has its demo trip is skipped.
    /// </summary>
    private async Task EnsureDemoTripsAsync()
    {
        var routes = (await _supabase.From<BusRoute>().Get()).Models
            .Where(r => !string.IsNullOrWhiteSpace(r.WaypointsJson))
            .ToList();
        if (routes.Count == 0)
            return;

        var activeTrips = (await _supabase
            .From<Trip>()
            .Filter("trip_status", Postgrest.Constants.Operator.Equals, "Active")
            .Get()).Models;

        // Routes that already have a demo bus, and vehicles committed to any active trip,
        // whether real or simulated, so no bus is double-booked.
        var routesWithDemo = activeTrips.Where(t => t.IsSimulated).Select(t => t.RouteId).ToHashSet();
        var busyVehicleIds = activeTrips
            .Where(t => t.VehicleId != null)
            .Select(t => t.VehicleId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var vehicles = (await _supabase.From<Vehicle>().Get()).Models;

        var driverId = await GetAnyDriverIdAsync();
        if (driverId is null)
        {
            _logger.LogWarning("No driver found to attach demo trips; skipping.");
            return;
        }

        foreach (var route in routes)
        {
            if (routesWithDemo.Contains(route.RouteId))
                continue;

            var v = vehicles.FirstOrDefault(x => x.RouteId == route.RouteId
                                              && !x.OutOfService
                                              && !busyVehicleIds.Contains(x.VehicleId));
            if (v is null)
                continue; // no free, deployable bus on this route
            busyVehicleIds.Add(v.VehicleId);

            // The trip identifier is generated by the database and must not be supplied.
            var trip = new Trip
            {
                Date = PhClock.Today,
                ShiftType = "Morning",
                ShiftStartTime = new TimeSpan(6, 0, 0),
                ShiftEndTime = new TimeSpan(14, 0, 0),
                RouteId = route.RouteId,
                VehicleId = v.VehicleId,
                DriverId = driverId.Value,
                TripStatus = "Active",
                EstimatedRevenue = 0,
                IsSimulated = true, // tag so the OFF switch deletes exactly what we made
            };

            await _supabase.From<Trip>().Insert(trip);
            _logger.LogInformation("Created demo trip on route {RouteId} with vehicle {VehicleId}.", route.RouteId, v.VehicleId);
        }
    }

    private async Task<int?> GetAnyDriverIdAsync()
    {
        if (_cachedDriverId is int cached)
            return cached;

        // Role 2 is the driver role. Falls back to any user when none is found.
        var drivers = (await _supabase
            .From<UserModel>()
            .Filter("role_id", Postgrest.Constants.Operator.Equals, "2")
            .Get()).Models;

        var driver = drivers.FirstOrDefault()
                     ?? (await _supabase.From<UserModel>().Get()).Models.FirstOrDefault();

        _cachedDriverId = driver?.UserId;
        return _cachedDriverId;
    }

    /// <summary>Advances one trip along its route by a single tick.</summary>
    private TripState AdvanceTrip(string tripId, RouteGeometry geometry, int capacity, int dbTotalBoarded)
    {
        if (!_states.TryGetValue(tripId, out var state))
        {
            // On first sighting the bus starts somewhere along the route with a plausible
            // load. Cumulative boardings are seeded from the stored total, so revenue
            // survives a restart, but never below the current load: everyone aboard boarded
            // at some point.
            var initialPassengers = _rng.Next(0, Math.Max(1, (int)(capacity * 0.6)));
            state = new TripState
            {
                DistanceMeters = _rng.NextDouble() * geometry.TotalLength,
                Passengers = initialPassengers,
                TotalBoarded = Math.Max(dbTotalBoarded, initialPassengers)
            };
            _states[tripId] = state;
        }

        // Advances by speed multiplied by the interval, looping at the end of the route.
        var speedKmh = MinSpeedKmh + _rng.NextDouble() * (MaxSpeedKmh - MinSpeedKmh);
        var metresPerTick = speedKmh / 3.6 * TickInterval.TotalSeconds;
        state.DistanceMeters = (state.DistanceMeters + metresPerTick) % geometry.TotalLength;
        state.SpeedKmh = speedKmh;

        var (lat, lng, heading) = geometry.LocateAt(state.DistanceMeters);
        state.Lat = lat;
        state.Lng = lng;
        state.Heading = heading;

        // Passenger numbers change on their own interval rather than every tick, so the bus
        // keeps moving smoothly while boardings accumulate at a believable rate.
        var now = DateTime.UtcNow;
        if (now - state.LastDriftUtc >= PassengerDriftInterval)
        {
            state.LastDriftUtc = now;

            // A small random change, clamped to the vehicle's capacity.
            var delta = _rng.Next(-MaxPassengerDelta, MaxPassengerDelta + 1);
            var newPassengers = Math.Clamp(state.Passengers + delta, 0, capacity);

            // Only increases count toward the cumulative total.
            var boarded = newPassengers - state.Passengers;
            if (boarded > 0)
                state.TotalBoarded += boarded;

            state.Passengers = newPassengers;
        }

        return state;
    }

    /// <summary>
    /// Matches the phone's write rule: a row is emitted on the first sighting, when the
    /// passenger count changes even while stationary, once the heartbeat interval elapses,
    /// or once the bus has moved far enough since the last written row.
    /// </summary>
    private static bool ShouldWrite(TripState s)
    {
        if (!s.HasWritten) return true;                                       // first row for this trip
        if (s.Passengers != s.LastWrittenPassengers) return true;            // boarding / alighting
        if (DateTime.UtcNow - s.LastWriteUtc >= WriteHeartbeat) return true; // heartbeat
        var moved = MetersBetween(s.LastWrittenLat, s.LastWrittenLng, s.Lat, s.Lng);
        return moved >= MinWriteMeters;                                       // moved enough
    }

    private static double MetersBetween(double lat1, double lng1, double lat2, double lng2)
    {
        const double earthRadius = 6_371_000; // metres
        var dLat = (lat2 - lat1) * Math.PI / 180.0;
        var dLng = (lng2 - lng1) * Math.PI / 180.0;
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return earthRadius * 2 * Math.Atan2(Math.Sqrt(h), Math.Sqrt(1 - h));
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

        // A null result is cached too, so a route without geometry is not queried every tick.
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

        // When passenger numbers last changed, which paces boarding accrual.
        public DateTime LastDriftUtc { get; set; } = DateTime.MinValue;

        // The last row actually written, which drives the write throttle.
        public bool HasWritten { get; set; }
        public DateTime LastWriteUtc { get; set; } = DateTime.MinValue;
        public double LastWrittenLat { get; set; }
        public double LastWrittenLng { get; set; }
        public int LastWrittenPassengers { get; set; } = int.MinValue;
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

            // A zero-length route cannot be animated.
            return cumulative[^1] > 0 ? new RouteGeometry(points, cumulative) : null;
        }

        /// <summary>Interpolate position and travel heading at a distance along the route.</summary>
        public (double Lat, double Lng, double Heading) LocateAt(double distanceMeters)
        {
            var d = Math.Clamp(distanceMeters, 0, TotalLength);

            // The segment containing the given distance.
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
