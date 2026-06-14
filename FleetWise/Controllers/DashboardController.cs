using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using FleetWise.Models;

namespace FleetWise.Controllers
{
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
                .Get();

            var todayTripIds = todayTrips.Select(t => t.TripId).ToHashSet();
            var yesterdayTripIds = yesterdayTrips.Select(t => t.TripId).ToHashSet();

            // For each trip, take the most recent telemetry record's passenger count
            var todayPassengers = telemetryResponse.Models
                .Where(t => todayTripIds.Contains(t.TripId))
                .GroupBy(t => t.TripId)
                .Sum(g => g.OrderByDescending(t => t.Timestamp).First().CurrentPassengers);

            var yesterdayPassengers = telemetryResponse.Models
                .Where(t => yesterdayTripIds.Contains(t.TripId))
                .GroupBy(t => t.TripId)
                .Sum(g => g.OrderByDescending(t => t.Timestamp).First().CurrentPassengers);

            // ── Passenger Demand Chart (hourly buckets) ───────────────
            var labelHours = new[] { 8, 9, 10, 11, 12, 13, 14 };

            var labels = labelHours
                .Select(h => h < 12 ? $"{h}:00 AM" : h == 12 ? "12:00 PM" : $"{h - 12}:00 PM")
                .ToList();

            var tripPassengerMap = telemetryResponse.Models
                .Where(t => todayTripIds.Contains(t.TripId))
                .GroupBy(t => t.TripId)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(t => t.Timestamp).First().CurrentPassengers
                );

            var data = labelHours.Select(h =>
            {
                var tripsInHour = todayTrips
                    .Where(t => t.ShiftStartTime.Hours == h)
                    .Select(t => t.TripId);

                return tripsInHour.Sum(id => tripPassengerMap.TryGetValue(id, out var p) ? p : 0);
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
