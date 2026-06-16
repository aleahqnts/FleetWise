using System.Text.Json;
using System.Text.Json.Serialization;
using FleetWise.Models;

namespace FleetWise.Services;

/// <summary>
/// Development-only stand-in producer for live telemetry (PLAN.md Block 9 / §2.3).
/// Every 5 seconds it advances each Active trip along its route geometry and inserts
/// one ordinary row into the Supabase <c>telemetry_data</c> table — the exact table
/// real IoT hardware or Chester's mobile app will later write to. The read path
/// (FleetMapController) never knows the data is simulated; cut-over = delete one
/// registration line in Program.cs (see Step 9.2).
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

    // Route geometry never changes mid-run, so it's cached after first read (§9.1).
    private readonly Dictionary<int, RouteGeometry> _geometryCache = new();

    // Per-trip simulation state lives in memory; on restart buses resume from the
    // route start, which is fine for a simulator (§9.1).
    private readonly Dictionary<string, TripState> _states = new();

    public TelemetrySimulator(Supabase.Client supabase, ILogger<TelemetrySimulator> logger)
    {
        _supabase = supabase;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The Supabase.Client is a singleton, so it is used directly here. There are no
        // *scoped* services to resolve in a tick, so no per-tick DI scope is created —
        // the captive-dependency hazard §9.1 warns about simply doesn't arise.
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

            var state = AdvanceTrip(trip.TripId, geometry, capacity);

            var telemetry = new TelemetryData
            {
                TripId = trip.TripId,
                Latitude = (decimal)state.Lat,
                Longitude = (decimal)state.Lng,
                CurrentPassengers = state.Passengers,
                Speed = Math.Round((decimal)state.SpeedKmh, 1),
                Heading = (float)state.Heading,
                Timestamp = DateTime.UtcNow
            };

            await _supabase.From<TelemetryData>().Insert(telemetry);
        }
    }

    /// <summary>Advance one trip along its route by one tick and return its new state.</summary>
    private TripState AdvanceTrip(string tripId, RouteGeometry geometry, int capacity)
    {
        if (!_states.TryGetValue(tripId, out var state))
        {
            // First sighting: start somewhere along the route with a plausible load.
            state = new TripState
            {
                DistanceMeters = _rng.NextDouble() * geometry.TotalLength,
                Passengers = _rng.Next(0, Math.Max(1, (int)(capacity * 0.6)))
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
        state.Passengers = Math.Clamp(state.Passengers + delta, 0, capacity);

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
