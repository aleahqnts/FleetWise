using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using FleetWise.Models;
using FleetWise.Services;

namespace FleetWise.Controllers
{
    [Authorize]
    public class DashboardController : Controller
    {
        private readonly Supabase.Client _supabase;

        public DashboardController(Supabase.Client supabase) => _supabase = supabase;

        public async Task<IActionResult> Index(int? routeId)
        {
            // ── Service day = current operating cycle (06:00 -> 05:59 next morning),
            //    not raw calendar day. Trips are dated their START day, so a cycle's
            //    trips are exactly those dated `today`. ──────────────────
            var today = PhClock.OperationalDay;
            var yesterday = today.AddDays(-1);

            // ── Flagged Vehicles (unaffected by filters) = buses with an OPEN incident
            //    (unresolved maintenance_log). The old vehicle_status=="Flagged" count read 0
            //    because the next shift's start/end overwrites that column. Matches the
            //    Dispatch + Vehicles definition. ─────────────────────────
            var maintResponse = await _supabase.From<MaintenanceLog>().Get();
            int flaggedVehicles = maintResponse.Models
                .Where(l => l.ResolvedAt == null && l.VehicleId != null)
                .Select(l => l.VehicleId)
                .Distinct()
                .Count();

            // ── Today's & yesterday's trips (base queries) ────────────
            var todayTripsResponse = await _supabase
                .From<Trip>()
                .Filter("date", Postgrest.Constants.Operator.Equals, today.ToString("yyyy-MM-dd"))
                .Get();

            var yesterdayTripsResponse = await _supabase
                .From<Trip>()
                .Filter("date", Postgrest.Constants.Operator.Equals, yesterday.ToString("yyyy-MM-dd"))
                .Get();

            // ── Apply route filter. Trips dated `today` already span the whole 06:00→06:00
            //    cycle (night shift dated its start day). Also fold in any STILL-ACTIVE trip
            //    dated yesterday that hasn't been ended yet, so a lingering overnight trip
            //    never vanishes from the dashboard when the cycle rolls. ──
            var todayTrips = todayTripsResponse.Models
                .Concat(yesterdayTripsResponse.Models.Where(t => t.TripStatus == "Active"))
                .Where(t => !routeId.HasValue || t.RouteId == routeId.Value)
                .GroupBy(t => t.TripId).Select(g => g.First())   // de-dupe
                .ToList();

            var yesterdayTrips = yesterdayTripsResponse.Models
                .Where(t => !routeId.HasValue || t.RouteId == routeId.Value)
                .ToList();

            // ── Active Trips ──────────────────────────────────────────
            int activeTrips = todayTrips.Count(t => t.TripStatus == "Active");

            // ── Revenue ───────────────────────────────────────────────
            decimal todayRevenue = todayTrips.Sum(t => t.EstimatedRevenue);
            decimal yesterdayRevenue = yesterdayTrips.Sum(t => t.EstimatedRevenue);

            // ── Passenger Count (from trips.total_boarded) ────────────
            var todayTripIds = todayTrips.Select(t => t.TripId).ToHashSet();
            var yesterdayTripIds = yesterdayTrips.Select(t => t.TripId).ToHashSet();

            int todayPassengers = todayTrips.Sum(t => t.TotalBoarded);
            int yesterdayPassengers = yesterdayTrips.Sum(t => t.TotalBoarded);

            // ── Telemetry feed (hourly chart only — TotalBoarded has no intra-day
            //    breakdown, so the demand chart still needs the raw telemetry stream) ──
            // Window = this service cycle: opDay 06:00 (inclusive) -> next day 06:00 (exclusive).
            var cycleStart = today.Add(PhClock.DayStartTime);
            var cycleEnd = today.AddDays(1).Add(PhClock.DayStartTime);
            var telemetryResponse = await _supabase
                .From<TelemetryData>()
                .Filter("timestamp", Postgrest.Constants.Operator.GreaterThanOrEqual,
                        cycleStart.ToString("yyyy-MM-dd HH:mm:ss"))
                .Filter("timestamp", Postgrest.Constants.Operator.LessThan,
                        cycleEnd.ToString("yyyy-MM-dd HH:mm:ss"))
                .Get();

            // ── Passenger Onboard Chart — hour marks across the full cycle ─────
            // 25 points: 06:00 today -> 06:00 next day (6am to 6am, both ends shown).
            var markTimes = Enumerable.Range(0, 25).Select(i => cycleStart.AddHours(i)).ToList();

            var labels = markTimes
                .Select(dt => dt.Hour switch
                {
                    0 => "12:00 AM",
                    12 => "12:00 PM",
                    < 12 => $"{dt.Hour}:00 AM",
                    _ => $"{dt.Hour - 12}:00 PM",
                })
                .ToList();

            // Stored timestamp digits are ALREADY PH wall-clock (PhClock.NowForDb) — never
            // add a +8 offset (double-shift bug).
            var todayTelemetry = telemetryResponse.Models
                .Where(t => todayTripIds.Contains(t.TripId))
                .ToList();
            var now = PhClock.Now;

            // We only record BOARDINGS (no alighting), so "occupancy" is unknowable. The chart
            // shows CUMULATIVE passengers boarded by hour — a count that only ever grows and
            // ends exactly at trips.total_boarded (the source of truth). Each trip's live
            // window uses ACTUAL start/end when the driver app set them, else the scheduled
            // shift (overnight rolls +1 day).
            static DateTime FloorHour(DateTime d) => new(d.Year, d.Month, d.Day, d.Hour, 0, 0);
            var tripWindows = todayTrips.Select(t =>
            {
                var schedStart = t.Date.Date + t.ShiftStartTime;
                var schedEnd = t.Date.Date + t.ShiftEndTime
                    + (t.ShiftEndTime <= t.ShiftStartTime ? TimeSpan.FromDays(1) : TimeSpan.Zero);
                // Postgrest deserializes timestamptz to a LOCAL-kind DateTime, shifting the
                // stored PH wall-clock digits +8h. Normalize back to UTC so the raw digits
                // (true PH time) line up with the Unspecified-kind hour marks — otherwise an
                // overnight trip shifts past the cycle window and vanishes from the chart.
                var start = t.ActualStartTime?.ToUniversalTime() ?? schedStart;
                var end = t.ActualEndTime?.ToUniversalTime() ?? (t.TripStatus == "Active" ? now : schedEnd);
                // Guard against clock skew / future timestamps (mobile may write a start
                // ahead of server time): never let the window start after "now", and floor
                // to the hour so the starting bucket is included. End can't exceed now.
                if (start > now) start = now;
                start = FloorHour(start);
                if (end > now) end = now;
                if (end < start) end = start;
                return new { Trip = t, Start = start, End = end };
            }).ToList();

            // For each hour mark sum every trip's cumulative boarded-so-far. Marks in the
            // future (> now) return null -> the line stops at the current time.
            var data = markTimes.Select(mark =>
            {
                if (mark > now) return (int?)null;
                int sum = 0;
                foreach (var w in tripWindows)
                {
                    if (mark < w.Start) continue;                       // trip hasn't started yet
                    if (mark > w.End) { sum += w.Trip.TotalBoarded; continue; } // ended earlier today -> count persists
                    // Cumulative boarded up to this hour: max telemetry reading seen so far
                    // (monotonic), capped at the trip's total. The trip's CURRENT hour is
                    // anchored to total_boarded so the line matches the KPI exactly.
                    int boarded = todayTelemetry
                        .Where(x => x.TripId == w.Trip.TripId && x.Timestamp.ToUniversalTime() <= mark)
                        .Select(x => x.TotalPassengers)
                        .DefaultIfEmpty(0)
                        .Max();
                    boarded = Math.Min(boarded, w.Trip.TotalBoarded);
                    if (mark.AddHours(1) > w.End) boarded = w.Trip.TotalBoarded; // last/current hour = truth
                    sum += boarded;
                }
                return (int?)sum;
            }).ToList();

            var maxVal = data.Where(d => d.HasValue).Select(d => d!.Value).DefaultIfEmpty(0).Max();
            int yMax = maxVal > 0 ? (int)(Math.Ceiling((maxVal + 50) / 100.0) * 100) : 400;
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

            // ── Passenger breakdown for ALL trips this cycle (Total Passengers modal) ──
            var routeNames = routesResponse.Models.ToDictionary(r => r.RouteId, r => r.RouteName);
            var tripBreakdown = todayTrips
                .OrderByDescending(t => t.TotalBoarded)
                .Select(t => new ActiveTripRow
                {
                    TripId = t.TripId,
                    RouteName = routeNames.TryGetValue(t.RouteId, out var rn) ? rn : $"Route {t.RouteId}",
                    VehicleId = t.VehicleId,
                    ShiftType = t.ShiftType,
                    Status = t.TripStatus,
                    Passengers = t.TotalBoarded,
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
                Today = today,
                ActiveTripBreakdown = tripBreakdown,
            };

            return View(vm);
        }
    }
}
