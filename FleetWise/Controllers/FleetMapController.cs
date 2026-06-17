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

        // Per-route terminals where non-running buses are shown parked (the JS spreads
        // each terminal's buses into a neat grid so the pills don't overlap). Routes
        // without an entry fall back to the first terminal.
        private static readonly Dictionary<int, (double Lat, double Lng, string Name)> Terminals = new()
        {
            [1] = (14.5466, 121.0285, "EDSA–Ayala Terminal"),
            [2] = (14.5095, 121.0465, "Arca South Terminal"),
        };

        private static (double Lat, double Lng, string Name) TerminalFor(int? routeId) =>
            routeId is int r && Terminals.TryGetValue(r, out var t) ? t : Terminals[1];

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

            // One fare lookup per poll (fare_config), shared across every bus below.
            var fareRate = await _fareCalculator.GetRateAsync();

            var positions = new List<BusPositionDto>();
            var movingVehicleIds = new HashSet<string>();

            // 1) Moving buses — one marker per vehicle, placed by its newest telemetry.
            // A vehicle may (in messy data) be on several Active trips at once; we keep
            // only the latest reading so its pill doesn't jump between trip positions.
            var movingByVehicle = new Dictionary<string, BusPositionDto>();
            foreach (var trip in activeTrips)
            {
                if (trip.VehicleId is null)
                    continue;
                if (!latestByTrip.TryGetValue(trip.TripId, out var telemetry))
                    continue; // no telemetry yet (simulator hasn't ticked for this trip)

                vehiclesById.TryGetValue(trip.VehicleId, out var vehicle);
                if (DisplayStatus(vehicle?.VehicleStatus) != "Active")
                    continue; // only "On Trip" buses run on the map; the rest park (below)

                if (movingByVehicle.TryGetValue(trip.VehicleId, out var existing) &&
                    existing.Timestamp >= telemetry.Timestamp)
                    continue; // an earlier trip already gave a newer position for this bus

                routesById.TryGetValue(trip.RouteId, out var route);
                usersById.TryGetValue(trip.DriverId, out var driver);

                var capacity = vehicle?.Capacity ?? 0;
                var passengers = telemetry.CurrentPassengers;
                var occupancyPct = capacity > 0
                    ? (int)Math.Round(passengers * 100.0 / capacity)
                    : 0;

                movingByVehicle[trip.VehicleId] = new BusPositionDto
                {
                    TripId = trip.TripId,
                    VehicleId = trip.VehicleId,
                    PlateNumber = vehicle?.PlateNumber ?? "—",
                    RouteId = trip.RouteId,
                    RouteName = route?.RouteName ?? "—",
                    Shift = FormatShift(trip),
                    DriverName = FormatDriverName(driver),
                    Status = "Active",
                    Lat = (double)telemetry.Latitude,
                    Lng = (double)telemetry.Longitude,
                    Heading = telemetry.Heading ?? 0,
                    Speed = (double)(telemetry.Speed ?? 0),
                    Passengers = passengers,
                    Capacity = capacity,
                    OccupancyPct = occupancyPct,
                    EstimatedRevenue = _fareCalculator.Estimate(passengers, fareRate),
                    Timestamp = telemetry.Timestamp
                };
            }

            positions.AddRange(movingByVehicle.Values);
            foreach (var id in movingByVehicle.Keys)
                movingVehicleIds.Add(id);

            // 2) Parked buses — every non-"On Trip" vehicle (Ready to Deploy / Pending /
            // Flagged / Idle / Offline …), shown stationary at the terminal.
            foreach (var vehicle in vehiclesResponse.Models)
            {
                var vehicleStatus = DisplayStatus(vehicle.VehicleStatus);
                if (vehicleStatus == "Active")
                    continue; // on-trip buses are shown moving, not parked
                if (movingVehicleIds.Contains(vehicle.VehicleId))
                    continue;
                if (routeId.HasValue && vehicle.RouteId != routeId.Value)
                    continue;

                routesById.TryGetValue(vehicle.RouteId ?? -1, out var route);
                var terminal = TerminalFor(vehicle.RouteId);

                positions.Add(new BusPositionDto
                {
                    TripId = null,
                    VehicleId = vehicle.VehicleId,
                    PlateNumber = vehicle.PlateNumber ?? "—",
                    RouteId = vehicle.RouteId ?? 0,
                    RouteName = route?.RouteName ?? "—",
                    Shift = "—",
                    DriverName = "Unassigned",
                    Status = vehicleStatus,
                    TerminalName = terminal.Name,
                    Lat = terminal.Lat,
                    Lng = terminal.Lng,
                    Heading = 0,
                    Speed = 0,
                    Passengers = 0,
                    Capacity = vehicle.Capacity,
                    OccupancyPct = 0,
                    EstimatedRevenue = 0,
                    Timestamp = PhClock.Now
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
        // ("Active" / "Idle" / "Offline"). Buses on the map are on live trips, so the
        // running state reads "Active" (matches Figures 18–19).
        private static string DisplayStatus(string? vehicleStatus) => vehicleStatus switch
        {
            null or "" => "Active",
            "OnTrip" or "On Trip" or "Active" => "Active",
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
