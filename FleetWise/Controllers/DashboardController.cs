using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using FleetWise.Models;

namespace FleetWise.Controllers
{
    [Authorize]
    public class DashboardController : Controller
    {
        private readonly Supabase.Client _supabase;

        public DashboardController(Supabase.Client supabase) => _supabase = supabase;

        public async Task<IActionResult> Index(int? routeId)
        {
            // ── Date is ALWAYS today — not user-configurable ──────────
            var today = DateTime.Today;
            var yesterday = today.AddDays(-1);

            // ── Flagged Vehicles (unaffected by filters) ──────────────
            var flaggedResponse = await _supabase
                .From<Vehicle>()
                .Filter("vehicle_status", Postgrest.Constants.Operator.Equals, "Flagged")
                .Get();

            int flaggedVehicles = flaggedResponse.Models.Count;

            // ── Today's & yesterday's trips (base queries) ────────────
            var todayTripsResponse = await _supabase
                .From<Trip>()
                .Filter("date", Postgrest.Constants.Operator.Equals, today.ToString("yyyy-MM-dd"))
                .Get();

            var yesterdayTripsResponse = await _supabase
                .From<Trip>()
                .Filter("date", Postgrest.Constants.Operator.Equals, yesterday.ToString("yyyy-MM-dd"))
                .Get();

            // ── Apply route filter ────────────────────────────────────
            var todayTrips = routeId.HasValue
                ? todayTripsResponse.Models.Where(t => t.RouteId == routeId.Value).ToList()
                : todayTripsResponse.Models;

            var yesterdayTrips = routeId.HasValue
                ? yesterdayTripsResponse.Models.Where(t => t.RouteId == routeId.Value).ToList()
                : yesterdayTripsResponse.Models;

            // ── Active Trips ──────────────────────────────────────────
            int activeTrips = todayTrips.Count(t => t.TripStatus == "Active");

            // ── Revenue ───────────────────────────────────────────────
            decimal todayRevenue = todayTrips.Sum(t => t.EstimatedRevenue);
            decimal yesterdayRevenue = yesterdayTrips.Sum(t => t.EstimatedRevenue);

            // ── Passenger Count (from telemetry_data) ─────────────────
            var telemetryResponse = await _supabase
                .From<TelemetryData>()
                .Filter("timestamp", Postgrest.Constants.Operator.GreaterThanOrEqual,
                        yesterday.ToString("yyyy-MM-dd"))   // catches 22:00–23:59 PHT yesterday
                .Filter("timestamp", Postgrest.Constants.Operator.LessThan,
                        today.AddDays(1).ToString("yyyy-MM-dd"))
                .Get();

            var todayTripIds = todayTrips.Select(t => t.TripId).ToHashSet();
            var yesterdayTripIds = yesterdayTrips.Select(t => t.TripId).ToHashSet();

            // For each trip, take the most recent telemetry record's passenger count
            var todayPassengers = telemetryResponse.Models
                .Where(t => todayTripIds.Contains(t.TripId))
                .GroupBy(t => t.TripId)
                .Sum(g => g.OrderByDescending(t => t.Timestamp).First().TotalPassengers);

            var yesterdayPassengers = telemetryResponse.Models
                .Where(t => yesterdayTripIds.Contains(t.TripId))
                .GroupBy(t => t.TripId)
                .Sum(g => g.OrderByDescending(t => t.Timestamp).First().TotalPassengers);

            // ── Passenger Demand Chart (hourly buckets) ───────────────
            // Covers all three shifts: 06:00–14:00, 14:00–22:00, 22:00–06:00
            var labelHours = new[] { 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 0, 1, 2, 3, 4, 5 };

            var labels = labelHours
                .Select(h => h == 0 ? "12:00 AM"
                    : h < 12 ? $"{h}:00 AM"
                    : h == 12 ? "12:00 PM"
                    : $"{h - 12}:00 PM")
                .ToList();

            // Bucket telemetry readings (for today's trips) by the local hour the reading
            // was recorded. Timestamps are stored in UTC; convert to PHT (UTC+8) for bucketing.
            var phtOffset = TimeSpan.FromHours(8);

            var todayTelemetry = telemetryResponse.Models
                .Where(t => todayTripIds.Contains(t.TripId));

            var data = labelHours.Select(h =>
            {
                return todayTelemetry
                    .Where(t => DateTime.SpecifyKind(t.Timestamp, DateTimeKind.Utc).Add(phtOffset).Hour == h)
                    .GroupBy(t => t.TripId)
                    .Sum(g => g.OrderByDescending(t => t.Timestamp).First().TotalPassengers);
            }).ToList();

            int yMax = data.Any(d => d > 0) ? (int)(Math.Ceiling((data.Max() + 50) / 100.0) * 100) : 400;
            int yStep = yMax / 4;

            // ── Routes dropdown ───────────────────────────────────────
            var routesResponse = await _supabase
                .From<BusRoute>()
                .Order("route_name", Postgrest.Constants.Ordering.Ascending)
                .Get();

            var routes = routesResponse.Models
                .Select(r => new SelectListItem
                {
                    Value = r.RouteId.ToString(),
                    Text = r.RouteName,
                    Selected = routeId.HasValue && r.RouteId == routeId.Value
                })
                .ToList();

            // ── Build ViewModel ───────────────────────────────────────
            var vm = new DashboardViewModel
            {
                ActiveTrips = activeTrips,
                FlaggedVehicles = flaggedVehicles,
                TotalPassengers = todayPassengers,
                PassengerDelta = todayPassengers - yesterdayPassengers,
                TotalRevenue = todayRevenue,
                RevenueDelta = todayRevenue - yesterdayRevenue,
                ChartLabels = labels,
                ChartData = data,
                ChartYMax = yMax,
                ChartYStep = yStep,
                Routes = routes,
                SelectedRouteId = routeId,
            };

            return View(vm);
        }
    }
}
