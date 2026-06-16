using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FleetWise.Models;
using FleetWise.Models.ViewModels;
using FleetWise.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FleetWise.Controllers
{
    [Authorize]
    public class FleetMapController : Controller
    {
        private readonly Supabase.Client _supabase;
        private readonly FareCalculator _fareCalculator;

        public FleetMapController(Supabase.Client supabase, FareCalculator fareCalculator)
        {
            _supabase = supabase;
            _fareCalculator = fareCalculator;
        }

        public async Task<IActionResult> Index()
        {
            var routesResponse = await _supabase.From<BusRoute>().Get();

            double? south = null, west = null, north = null, east = null;

            foreach (var route in routesResponse.Models)
            {
                if (string.IsNullOrWhiteSpace(route.WaypointsJson))
                    continue;

                var waypoints = JsonSerializer.Deserialize<List<WaypointDto>>(route.WaypointsJson);
                if (waypoints is null)
                    continue;

                foreach (var point in waypoints)
                {
                    south = south is null ? point.Lat : Math.Min(south.Value, point.Lat);
                    north = north is null ? point.Lat : Math.Max(north.Value, point.Lat);
                    west = west is null ? point.Lng : Math.Min(west.Value, point.Lng);
                    east = east is null ? point.Lng : Math.Max(east.Value, point.Lng);
                }
            }

            ViewBag.MapBounds = south is not null
                ? new[] { south.Value, west!.Value, north!.Value, east!.Value }
                : null;

            return View();
        }

        public async Task<IActionResult> Stops(int? routeId)
        {
            var routesResponse = await _supabase.From<BusRoute>().Get();
            var stops = new List<StopDto>();

            foreach (var route in routesResponse.Models)
            {
                if (routeId.HasValue && route.RouteId != routeId.Value)
                    continue;

                if (string.IsNullOrWhiteSpace(route.StopsJson))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(route.StopsJson);
                    var root = doc.RootElement;

                    if (root.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var stopElement in root.EnumerateArray())
                        {
                            if (stopElement.TryGetProperty("name", out var nameElement) &&
                                stopElement.TryGetProperty("lat", out var latElement) &&
                                stopElement.TryGetProperty("lng", out var lngElement) &&
                                latElement.TryGetDouble(out var lat) &&
                                lngElement.TryGetDouble(out var lng))
                            {
                                stops.Add(new StopDto
                                {
                                    Name = nameElement.GetString() ?? "Unknown Stop",
                                    Lat = lat,
                                    Lng = lng,
                                    RouteName = route.RouteName
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error parsing stops for route {route.RouteId}: {ex.Message}");
                }
            }

            return Json(stops);
        }

        public async Task<IActionResult> Routes()
        {
            var routesResponse = await _supabase.From<BusRoute>().Get();
            var routeData = routesResponse.Models.Select(r => new
            {
                r.RouteId,
                r.RouteName,
                r.WaypointsJson
            }).ToList();

            return Json(routeData);
        }

        // Live bus positions: latest telemetry row per Active trip, joined in C# to
        // vehicle/route/driver, with occupancy % and revenue computed server-side so
        // every consumer (markers, tooltip, side panel) shows identical numbers (§2.7).
        public async Task<IActionResult> Positions(int? routeId, string? status)
        {
            var tripsResponse = await _supabase
                .From<Trip>()
                .Filter("trip_status", Postgrest.Constants.Operator.Equals, "Active")
                .Get();

            var activeTrips = tripsResponse.Models;
            if (routeId.HasValue)
                activeTrips = activeTrips.Where(t => t.RouteId == routeId.Value).ToList();

            if (activeTrips.Count == 0)
                return Json(Array.Empty<BusPositionDto>());

            var activeTripIds = activeTrips.Select(t => t.TripId).ToHashSet();

            // Newest-first: Supabase caps a plain .Get() at 1000 rows and returns the
            // OLDEST ones, so on a table with lots of history the "latest per trip"
            // below would be permanently stale. Ordering by timestamp descending makes
            // .Get() return the most recent rows, which is all we need here.
            var telemetryResponse = await _supabase
                .From<TelemetryData>()
                .Order("timestamp", Postgrest.Constants.Ordering.Descending)
                .Get();
            var vehiclesResponse = await _supabase.From<Vehicle>().Get();
            var routesResponse = await _supabase.From<BusRoute>().Get();
            var usersResponse = await _supabase.From<UserModel>().Get();

            // Latest telemetry row per active trip.
            var latestByTrip = telemetryResponse.Models
                .Where(t => activeTripIds.Contains(t.TripId))
                .GroupBy(t => t.TripId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.Timestamp).First());

            var vehiclesById = vehiclesResponse.Models
                .ToDictionary(v => v.VehicleId, v => v);
            var routesById = routesResponse.Models
                .ToDictionary(r => r.RouteId, r => r);
            var usersById = usersResponse.Models
                .ToDictionary(u => u.UserId, u => u);

            var positions = new List<BusPositionDto>();

            foreach (var trip in activeTrips)
            {
                if (!latestByTrip.TryGetValue(trip.TripId, out var telemetry))
                    continue; // no telemetry yet (simulator hasn't ticked for this trip)

                vehiclesById.TryGetValue(trip.VehicleId ?? string.Empty, out var vehicle);
                routesById.TryGetValue(trip.RouteId, out var route);
                usersById.TryGetValue(trip.DriverId, out var driver);

                var capacity = vehicle?.Capacity ?? 0;
                var passengers = telemetry.CurrentPassengers;
                var occupancyPct = capacity > 0
                    ? (int)Math.Round(passengers * 100.0 / capacity)
                    : 0;

                positions.Add(new BusPositionDto
                {
                    TripId = trip.TripId,
                    VehicleId = trip.VehicleId,
                    PlateNumber = vehicle?.PlateNumber ?? "—",
                    RouteId = trip.RouteId,
                    RouteName = route?.RouteName ?? "—",
                    Shift = FormatShift(trip),
                    DriverName = FormatDriverName(driver),
                    Status = DisplayStatus(vehicle?.VehicleStatus),
                    Lat = (double)telemetry.Latitude,
                    Lng = (double)telemetry.Longitude,
                    Heading = telemetry.Heading ?? 0,
                    Speed = (double)(telemetry.Speed ?? 0),
                    Passengers = passengers,
                    Capacity = capacity,
                    OccupancyPct = occupancyPct,
                    EstimatedRevenue = _fareCalculator.Estimate(passengers),
                    Timestamp = telemetry.Timestamp
                });
            }

            if (!string.IsNullOrWhiteSpace(status))
                positions = positions.Where(p =>
                    string.Equals(p.Status, status, StringComparison.OrdinalIgnoreCase)).ToList();

            return Json(positions);
        }

        // "6AM – 12PM" from the trip's shift start/end times (Figure 19 header).
        private static string FormatShift(Trip trip)
        {
            static string Fmt(TimeSpan t) =>
                DateTime.Today.Add(t).ToString("htt", CultureInfo.InvariantCulture);

            return $"{Fmt(trip.ShiftStartTime)} – {Fmt(trip.ShiftEndTime)}";
        }

        private static string FormatDriverName(UserModel? driver)
        {
            if (driver is null)
                return "Unassigned";

            var name = $"{driver.FirstName} {driver.LastName}".Trim();
            return string.IsNullOrEmpty(name) ? "Unassigned" : name;
        }

        // Normalize the stored vehicle_status to the labels the Status filter shows
        // (Block 7: "On Trip" / "Idle" / "Offline").
        private static string DisplayStatus(string? vehicleStatus) => vehicleStatus switch
        {
            null or "" => "On Trip",
            "OnTrip" or "On Trip" or "Active" => "On Trip",
            "Idle" => "Idle",
            "Offline" => "Offline",
            _ => vehicleStatus
        };

        private class WaypointDto
        {
            [JsonPropertyName("lat")]
            public double Lat { get; set; }

            [JsonPropertyName("lng")]
            public double Lng { get; set; }
        }
    }
}
