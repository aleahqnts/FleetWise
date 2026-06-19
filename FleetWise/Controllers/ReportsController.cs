using FleetWise.Models;
using FleetWise.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using static Postgrest.Constants;


namespace FleetWise.Controllers
{
    [Authorize]
    public class ReportsController : Controller
    {
        private readonly Supabase.Client _supabase;
        private const int PageSize = 6;

        public ReportsController(Supabase.Client supabase) => _supabase = supabase;

        public async Task<IActionResult> Index() => View();

        // ── Main data endpoint for the Reports page ──────────────────
        // Returns stat cards, table page, pagination, and the passenger /
        // revenue summary cards (mini chart + top routes), all filtered
        // by the global Route/Date filters and the per-card period selects.
        [HttpGet]
        public async Task<IActionResult> GetData(
            int? routeId,
            DateTime? date,
            int page = 1,
            string passengerPeriod = "This Week",
            string revenuePeriod = "This Week")
        {
            if (page < 1) page = 1;
            // Default to the current operating cycle (06:00->05:59 next day), not raw
            // calendar day — before 6 AM we're still inside yesterday's service day.
            var anchor = (date ?? PhClock.OperationalDay).Date;

            // ── Reference data ─────────────────────────────────────
            var routesResp = await _supabase.From<BusRoute>().Get();
            var routes = routesResp.Models;
            var routeNames = routes.ToDictionary(r => r.RouteId, r => r.RouteName);

            var usersResp = await _supabase.From<UserModel>().Get();
            var userNames = usersResp.Models.ToDictionary(u => u.UserId, u => $"{u.FirstName} {u.LastName}");

            var vehiclesResp = await _supabase.From<Vehicle>().Get();
            var vehiclesById = vehiclesResp.Models.ToDictionary(v => v.VehicleId, v => v);

            var tripsResp = await _supabase.From<Trip>().Get();
            var allTrips = tripsResp.Models;

            int Passengers(Trip t) => t.TotalBoarded;

            bool MatchesGlobalFilters(Trip t) =>
                (!routeId.HasValue || routeId.Value == 0 || t.RouteId == routeId.Value) &&
                (!date.HasValue || t.Date.Date == anchor);

            // ── Table (Daily Trip Reports) ─────────────────────────
            var filtered = allTrips
                .Where(MatchesGlobalFilters)
                .OrderByDescending(t => t.Date)
                .ThenBy(t => t.TripId)
                .ToList();

            int totalCount = filtered.Count;
            int totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)PageSize));
            if (page > totalPages) page = totalPages;
            var pageTrips = filtered.Skip((page - 1) * PageSize).Take(PageSize).ToList();

            var tableRows = pageTrips.Select(t => new
            {
                tripId = t.TripId,
                driverName = userNames.TryGetValue(t.DriverId, out var dn) ? dn : "N/A",
                vehicleId = t.VehicleId,
                plateNumber = vehiclesById.TryGetValue(t.VehicleId, out var v) ? v.PlateNumber : "N/A",
                routeName = routeNames.TryGetValue(t.RouteId, out var rn) ? rn : "N/A",
                shiftType = t.ShiftType,
                passengers = Passengers(t),
                revenue = t.EstimatedRevenue,
                status = t.TripStatus
            });

            // ── Stat cards ──────────────────────────────────────────
            int completedTrips = filtered.Count(t => string.Equals(t.TripStatus, "completed", StringComparison.OrdinalIgnoreCase));
            int delayedTrips = filtered.Count(t =>
                string.Equals(t.TripStatus, "delayed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.TripStatus, "missed", StringComparison.OrdinalIgnoreCase));

            int totalPassengers = filtered.Sum(Passengers);
            decimal totalRevenue = filtered.Sum(t => t.EstimatedRevenue);

            var prevDay = anchor.AddDays(-1);
            var prevDayTrips = allTrips.Where(t =>
                (!routeId.HasValue || routeId.Value == 0 || t.RouteId == routeId.Value) &&
                t.Date.Date == prevDay).ToList();

            int prevPassengers = prevDayTrips.Sum(Passengers);
            decimal prevRevenue = prevDayTrips.Sum(t => t.EstimatedRevenue);

            // ── Summary cards (passenger / revenue) ────────────────
            var passengerSummary = BuildSummary(allTrips, Passengers, routeNames, routeId, anchor, passengerPeriod, isPassenger: true);
            var revenueSummary = BuildSummary(allTrips, Passengers, routeNames, routeId, anchor, revenuePeriod, isPassenger: false);

            return Json(new
            {
                routes = routes
                    .OrderBy(r => r.RouteId)
                    .Select(r => new { id = r.RouteId, name = r.RouteName }),

                stats = new
                {
                    totalTrips = completedTrips,
                    delayedTrips,
                    totalPassengers,
                    totalRevenue,
                    passengerDelta = totalPassengers - prevPassengers,
                    revenueDelta = totalRevenue - prevRevenue
                },

                table = new
                {
                    rows = tableRows,
                    page,
                    totalPages,
                    totalCount,
                    from = totalCount == 0 ? 0 : (page - 1) * PageSize + 1,
                    to = Math.Min(page * PageSize, totalCount)
                },

                passengerSummary,
                revenueSummary
            });
        }

        private static object BuildSummary(
            List<Trip> allTrips,
            Func<Trip, int> passengers,
            Dictionary<int, string> routeNames,
            int? routeId,
            DateTime anchor,
            string period,
            bool isPassenger)
        {
            var (start, end, prevStart, prevEnd) = GetRange(anchor, period);

            bool InRoute(Trip t) => !routeId.HasValue || routeId.Value == 0 || t.RouteId == routeId.Value;

            var current = allTrips.Where(t => InRoute(t) && t.Date.Date >= start && t.Date.Date <= end).ToList();
            var previous = allTrips.Where(t => InRoute(t) && t.Date.Date >= prevStart && t.Date.Date <= prevEnd).ToList();

            decimal total = isPassenger ? current.Sum(passengers) : current.Sum(t => t.EstimatedRevenue);
            decimal prevTotal = isPassenger ? previous.Sum(passengers) : previous.Sum(t => t.EstimatedRevenue);

            double deltaPct = prevTotal == 0
                ? (total == 0 ? 0 : 100)
                : (double)((total - prevTotal) / prevTotal * 100);

            // 7-day (Mon–Sun) series for the mini line chart — the week containing the anchor date
            int offset = ((int)anchor.DayOfWeek + 6) % 7; // days since Monday
            var weekStart = anchor.AddDays(-offset);
            var weekData = new decimal[7];
            for (int i = 0; i < 7; i++)
            {
                var day = weekStart.AddDays(i);
                var dayTrips = allTrips.Where(t => InRoute(t) && t.Date.Date == day);
                weekData[i] = isPassenger ? dayTrips.Sum(passengers) : dayTrips.Sum(t => t.EstimatedRevenue);
            }

            // Top 3 routes within the selected period
            var topRoutes = current
                .GroupBy(t => t.RouteId)
                .Select(g => new
                {
                    routeId = g.Key,
                    name = routeNames.TryGetValue(g.Key, out var rn) ? rn : $"Route {g.Key:00}",
                    value = isPassenger ? (decimal)g.Sum(passengers) : g.Sum(t => t.EstimatedRevenue)
                })
                .OrderByDescending(r => r.value)
                .Take(3)
                .ToList();

            decimal topSum = topRoutes.Sum(r => r.value);
            var topRoutesWithPct = topRoutes.Select(r => new
            {
                r.routeId,
                r.name,
                value = r.value,
                pct = topSum == 0 ? 0 : Math.Round((double)(r.value / topSum * 100))
            });

            return new
            {
                total,
                deltaPct = Math.Round(deltaPct, 1),
                comparisonLabel = period switch
                {
                    "This Day" => "vs Yesterday",
                    "Last Week" => "vs Previous Week",
                    "This Month" => "vs Last Month",
                    _ => "vs Last Week"
                },
                weekLabels = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" },
                weekData,
                topRoutes = topRoutesWithPct
            };
        }

        private static (DateTime start, DateTime end, DateTime prevStart, DateTime prevEnd) GetRange(DateTime anchor, string period)
        {
            switch (period)
            {
                case "This Day":
                    return (anchor, anchor, anchor.AddDays(-1), anchor.AddDays(-1));

                case "Last Week":
                    {
                        int offset = ((int)anchor.DayOfWeek + 6) % 7;
                        var thisWeekStart = anchor.AddDays(-offset);
                        var lastWeekStart = thisWeekStart.AddDays(-7);
                        var lastWeekEnd = thisWeekStart.AddDays(-1);
                        return (lastWeekStart, lastWeekEnd, lastWeekStart.AddDays(-7), lastWeekEnd.AddDays(-7));
                    }

                case "This Month":
                    {
                        var monthStart = new DateTime(anchor.Year, anchor.Month, 1);
                        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
                        var prevMonthStart = monthStart.AddMonths(-1);
                        var prevMonthEnd = monthStart.AddDays(-1);
                        return (monthStart, monthEnd, prevMonthStart, prevMonthEnd);
                    }

                default: // "This Week"
                    {
                        int offset = ((int)anchor.DayOfWeek + 6) % 7;
                        var weekStart = anchor.AddDays(-offset);
                        var weekEnd = weekStart.AddDays(6);
                        return (weekStart, weekEnd, weekStart.AddDays(-7), weekEnd.AddDays(-7));
                    }
            }
        }

        // ── Called by the View button via fetch() — returns JSON for the modal ──
        [HttpGet]
        public async Task<IActionResult> TripDetail(string tripId)
        {
            if (string.IsNullOrEmpty(tripId))
                return BadRequest("Trip ID is required.");

            Trip? tripResponse;
            try
            {
                tripResponse = await _supabase
                    .From<Trip>()
                    .Filter("trip_id", Operator.Equals, tripId)
                    .Single();
            }
            catch
            {
                return NotFound("Trip not found.");
            }

            if (tripResponse is null)
                return NotFound("Trip not found.");

            UserModel? driverResponse = null;
            try
            {
                driverResponse = await _supabase
                    .From<UserModel>()
                    .Filter("user_id", Operator.Equals, tripResponse.DriverId.ToString())
                    .Single();
            }
            catch { /* driver may not exist */ }

            Vehicle? vehicleResponse = null;
            try
            {
                vehicleResponse = await _supabase
                    .From<Vehicle>()
                    .Filter("vehicle_id", Operator.Equals, tripResponse.VehicleId)
                    .Single();
            }
            catch { /* vehicle may not exist */ }

            BusRoute? routeResponse = null;
            try
            {
                routeResponse = await _supabase
                    .From<BusRoute>()
                    .Filter("route_id", Operator.Equals, tripResponse.RouteId.ToString())
                    .Single();
            }
            catch { /* route may not exist */ }

            var telemetryResponse = await _supabase
                .From<TelemetryData>()
                .Filter("trip_id", Operator.Equals, tripId)
                .Order("timestamp", Ordering.Descending)
                .Limit(1)
                .Get();

            var latestTelemetry = telemetryResponse.Models.FirstOrDefault();

            // Telemetry total_passengers is sparse/often 0 for real driver trips. Fall back to
            // trips.total_boarded (source of truth) so the modal never shows 0 when the trip
            // actually boarded passengers. (Same flat-0 bug fixed on the dashboard.)
            var liveBoarded = latestTelemetry?.TotalPassengers ?? 0;
            if (liveBoarded <= 0) liveBoarded = tripResponse.TotalBoarded;

            var result = new
            {
                tripId = tripResponse.TripId,
                shiftType = tripResponse.ShiftType,
                shiftStart = ShiftStartAt(tripResponse).ToString("hh:mm tt"),
                shiftEnd = ShiftEndLabel(tripResponse),
                routeName = routeResponse?.RouteName ?? "N/A",
                vehicleType = "Bus", // vehicle_type column dropped — every unit is a bus
                vehicleId = vehicleResponse?.VehicleId ?? "N/A",
                plateNumber = vehicleResponse?.PlateNumber ?? "N/A",
                driverName = driverResponse != null
                    ? $"{driverResponse.FirstName} {driverResponse.LastName}"
                    : "N/A",
                driverId = tripResponse.DriverId,
                totalPassengers = liveBoarded,
                estimatedRevenue = tripResponse.EstimatedRevenue,
                tripStatus = tripResponse.TripStatus,
                date = tripResponse.Date.ToString("MMMM dd, yyyy")
            };

            return Json(result);
        }
        // ── Filter options for the report generation modal ────────────
        [HttpGet]
        public async Task<IActionResult> GetFilterOptions()
        {
            var routesResp = await _supabase.From<BusRoute>().Order("route_name", Postgrest.Constants.Ordering.Ascending).Get();
            var usersResp = await _supabase.From<UserModel>().Get();
            var vehiclesResp = await _supabase.From<Vehicle>().Get();
            var tripsResp = await _supabase.From<Trip>().Get();

            // Driver role ID is 2
            var driverIds = usersResp.Models
                .Where(u => u.RoleId == 2)
                .Select(u => u.UserId)
                .ToHashSet();

            // Map each driver to the routes they've driven (from trips)
            var driverRoutes = tripsResp.Models
                .Where(t => driverIds.Contains(t.DriverId))
                .GroupBy(t => t.DriverId)
                .ToDictionary(g => g.Key, g => g.Select(t => t.RouteId).Distinct().ToList());

            // Map each vehicle to the routes it's been on (from trips)
            var vehicleRoutes = tripsResp.Models
                .GroupBy(t => t.VehicleId)
                .ToDictionary(g => g.Key, g => g.Select(t => t.RouteId).Distinct().ToList());

            var drivers = usersResp.Models
                .Where(u => u.RoleId == 2)
                .OrderBy(u => u.LastName)
                .Select(u => new
                {
                    id = u.UserId,
                    name = $"{u.FirstName} {u.LastName}",
                    routeIds = driverRoutes.TryGetValue(u.UserId, out var r) ? r : new List<int>()
                });

            var vehicles = vehiclesResp.Models
                .OrderBy(v => v.VehicleId)
                .Select(v => new
                {
                    id = v.VehicleId,
                    label = $"{v.VehicleId} — {v.PlateNumber}",
                    routeIds = vehicleRoutes.TryGetValue(v.VehicleId, out var r) ? r : new List<int>()
                });

            var routes = routesResp.Models
                .Select(r => new { id = r.RouteId, name = r.RouteName });

            return Json(new { routes, drivers, vehicles });
        }

        // ── Generate report preview (returns grouped JSON) ────────────
        [HttpGet]
        public async Task<IActionResult> GenerateReport(
            string reportType,
            DateTime? date,
            int? routeId,
            int? driverId,
            string? vehicleId)
        {
            // Default to the current operating cycle (06:00->05:59 next day), not raw
            // calendar day — before 6 AM we're still inside yesterday's service day.
            var anchor = (date ?? PhClock.OperationalDay).Date;

            // ── Reference data ────────────────────────────────────────
            var routesResp = await _supabase.From<BusRoute>().Get();
            var routeNames = routesResp.Models.ToDictionary(r => r.RouteId, r => r.RouteName);

            var usersResp = await _supabase.From<UserModel>().Get();
            var userNames = usersResp.Models.ToDictionary(u => u.UserId, u => $"{u.FirstName} {u.LastName}");

            var vehiclesResp = await _supabase.From<Vehicle>().Get();
            var vehiclesById = vehiclesResp.Models.ToDictionary(v => v.VehicleId, v => v);

            var tripsResp = await _supabase.From<Trip>().Get();
            var allTrips = tripsResp.Models;

            int Passengers(Trip t) => t.TotalBoarded;

            // ── Apply filters ─────────────────────────────────────────
            var filtered = allTrips
                .Where(t => t.Date.Date == anchor)
                .Where(t => !routeId.HasValue || t.RouteId == routeId.Value)
                .Where(t => !driverId.HasValue || t.DriverId == driverId.Value)
                .Where(t => string.IsNullOrEmpty(vehicleId) || t.VehicleId == vehicleId)
                .OrderBy(t => t.RouteId)
                .ThenBy(t => t.ShiftStartTime)
                .ToList();

            object groups = reportType switch
            {
                "Passenger" => BuildPassengerReport(filtered, routeNames, userNames, Passengers),
                "Revenue" => BuildRevenueReport(filtered, routeNames, userNames, vehiclesById),
                _ => BuildDailyTripReport(filtered, routeNames, userNames, vehiclesById, Passengers),
            };

            // Grand totals across all filtered trips (rendered as a clear total at the end).
            var totals = new
            {
                trips = filtered.Count,
                passengers = filtered.Sum(Passengers),
                revenue = filtered.Sum(t => t.EstimatedRevenue),
            };

            return Json(new { groups, totals });
        }

        private static object BuildDailyTripReport(
            List<Trip> trips,
            Dictionary<int, string> routeNames,
            Dictionary<int, string> userNames,
            Dictionary<string, Vehicle> vehiclesById,
            Func<Trip, int> passengers)
        {
            var groups = trips
                .GroupBy(t => routeNames.TryGetValue(t.RouteId, out var rn) ? rn : $"Route {t.RouteId}")
                .Select(g => new
                {
                    groupName = g.Key,
                    columns = new[] { "Trip ID", "Date", "Driver", "Bus ID", "Shift", "Shift Time", "Passengers", "Revenue" },
                    rows = g.Select(t => new[]
                    {
                        t.TripId,
                        t.Date.ToString("MMM dd, yyyy"),
                        userNames.TryGetValue(t.DriverId, out var dn) ? dn : "N/A",
                        vehiclesById.TryGetValue(t.VehicleId, out var v) ? v.PlateNumber : t.VehicleId,
                        t.ShiftType ?? "",
                        ShiftRange(t),
                        passengers(t).ToString(),
                        $"₱{t.EstimatedRevenue:N2}"
                    }).ToList()
                })
                .ToList();

            return groups;
        }

        private static object BuildPassengerReport(
            List<Trip> trips,
            Dictionary<int, string> routeNames,
            Dictionary<int, string> userNames,
            Func<Trip, int> passengers)
        {
            var groups = trips
                .GroupBy(t => routeNames.TryGetValue(t.RouteId, out var rn) ? rn : $"Route {t.RouteId}")
                .Select(g => new
                {
                    groupName = g.Key,
                    columns = new[] { "Trip ID", "Date", "Driver", "Shift", "Shift Time", "Passengers" },
                    rows = g.Select(t => new[]
                    {
                        t.TripId,
                        t.Date.ToString("MMM dd, yyyy"),
                        userNames.TryGetValue(t.DriverId, out var dn) ? dn : "N/A",
                        t.ShiftType ?? "",
                        ShiftRange(t),
                        passengers(t).ToString()
                    }).ToList()
                })
                .ToList();

            return groups;
        }

        private static object BuildRevenueReport(
            List<Trip> trips,
            Dictionary<int, string> routeNames,
            Dictionary<int, string> userNames,
            Dictionary<string, Vehicle> vehiclesById)
        {
            var groups = trips
                .GroupBy(t => routeNames.TryGetValue(t.RouteId, out var rn) ? rn : $"Route {t.RouteId}")
                .Select(g => new
                {
                    groupName = g.Key,
                    columns = new[] { "Trip ID", "Date", "Driver", "Bus ID", "Shift", "Estimated Revenue" },
                    rows = g.Select(t => new[]
                    {
                        t.TripId,
                        t.Date.ToString("MMM dd, yyyy"),
                        userNames.TryGetValue(t.DriverId, out var dn) ? dn : "N/A",
                        vehiclesById.TryGetValue(t.VehicleId, out var v) ? v.PlateNumber : t.VehicleId,
                        t.ShiftType ?? "",
                        $"₱{t.EstimatedRevenue:N2}"
                    }).ToList()
                })
                .ToList();

            return groups;
        }

        // ── Download report as PDF (QuestPDF) ────────────────────────
        [HttpGet]
        public async Task<IActionResult> DownloadReport(
            string reportType,
            DateTime? date,
            int? routeId,
            int? driverId,
            string? vehicleId)
        {
            // Default to the current operating cycle (06:00->05:59 next day), not raw
            // calendar day — before 6 AM we're still inside yesterday's service day.
            var anchor = (date ?? PhClock.OperationalDay).Date;

            var routesResp = await _supabase.From<BusRoute>().Get();
            var routeNames = routesResp.Models.ToDictionary(r => r.RouteId, r => r.RouteName);

            var usersResp = await _supabase.From<UserModel>().Get();
            var userNames = usersResp.Models.ToDictionary(u => u.UserId, u => $"{u.FirstName} {u.LastName}");

            var vehiclesResp = await _supabase.From<Vehicle>().Get();
            var vehiclesById = vehiclesResp.Models.ToDictionary(v => v.VehicleId, v => v);

            var tripsResp = await _supabase.From<Trip>().Get();

            int Passengers(Trip t) => t.TotalBoarded;

            var filtered = tripsResp.Models
                .Where(t => t.Date.Date == anchor)
                .Where(t => !routeId.HasValue || t.RouteId == routeId.Value)
                .Where(t => !driverId.HasValue || t.DriverId == driverId.Value)
                .Where(t => string.IsNullOrEmpty(vehicleId) || t.VehicleId == vehicleId)
                .OrderBy(t => t.RouteId)
                .ThenBy(t => t.ShiftStartTime)
                .ToList();

            // ── Build report data ─────────────────────────────────────
            string reportTitle = reportType switch
            {
                "Passenger" => "Passenger Reports",
                "Revenue" => "Revenue Reports",
                _ => "Daily Trip Reports"
            };

            string fileName = reportType switch
            {
                "Passenger" => $"PassengerReport_{anchor:yyyy-MM-dd}.pdf",
                "Revenue" => $"RevenueReport_{anchor:yyyy-MM-dd}.pdf",
                _ => $"DailyTripReport_{anchor:yyyy-MM-dd}.pdf"
            };

            string[] columns = reportType switch
            {
                "Passenger" => new[] { "Trip ID", "Date", "Driver", "Route", "Shift", "Shift Time", "Passengers" },
                "Revenue" => new[] { "Trip ID", "Date", "Driver", "Bus ID", "Route", "Shift", "Est. Revenue" },
                _ => new[] { "Trip ID", "Date", "Driver", "Bus ID", "Route", "Shift", "Actual Start", "Actual End", "Passengers", "Revenue" }
            };

            Func<Trip, string[]> rowBuilder = reportType switch
            {
                "Passenger" => t => new[]
                {
                    t.TripId,
                    t.Date.ToString("MMM dd, yyyy"),
                    userNames.TryGetValue(t.DriverId, out var dn) ? dn : "N/A",
                    routeNames.TryGetValue(t.RouteId, out var rn) ? rn : "N/A",
                    t.ShiftType ?? "",
                    ShiftRange(t),
                    Passengers(t).ToString()
                },
                "Revenue" => t => new[]
                {
                    t.TripId,
                    t.Date.ToString("MMM dd, yyyy"),
                    userNames.TryGetValue(t.DriverId, out var dn) ? dn : "N/A",
                    vehiclesById.TryGetValue(t.VehicleId, out var v) ? v.PlateNumber : t.VehicleId,
                    routeNames.TryGetValue(t.RouteId, out var rn) ? rn : "N/A",
                    t.ShiftType ?? "",
                    $"₱{t.EstimatedRevenue:N2}"
                },
                _ => t => new[]
                {
                    t.TripId,
                    t.Date.ToString("MMM dd, yyyy"),
                    userNames.TryGetValue(t.DriverId, out var dn) ? dn : "N/A",
                    vehiclesById.TryGetValue(t.VehicleId, out var v) ? v.PlateNumber : t.VehicleId,
                    routeNames.TryGetValue(t.RouteId, out var rn) ? rn : "N/A",
                    t.ShiftType ?? "",
                    FmtActual(t.ActualStartTime),
                    FmtActual(t.ActualEndTime),
                    Passengers(t).ToString(),
                    $"₱{t.EstimatedRevenue:N2}"
                }
            };

            // Groups: keyed by route name
            var groups = filtered
                .GroupBy(t => routeNames.TryGetValue(t.RouteId, out var rn) ? rn : $"Route {t.RouteId}")
                .Select(g => new ReportGroup
                {
                    GroupName = g.Key,
                    Rows = g.Select(rowBuilder).ToList()
                })
                .ToList();

            // ── Generate PDF with QuestPDF ────────────────────────────
            QuestPDF.Settings.License = LicenseType.Community;

            var accentColor = reportType switch
            {
                "Passenger" => "#3B82F6",
                "Revenue" => "#D63384",
                _ => "#27AE60"
            };

            var pdfBytes = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4.Landscape());
                    page.Margin(32, Unit.Point);
                    page.DefaultTextStyle(x => x.FontFamily("Arial").FontSize(9).FontColor("#2D3748"));

                    // ── Header ────────────────────────────────────────
                    page.Header().Column(col =>
                    {
                        col.Item().Row(row =>
                        {
                            row.RelativeItem().Column(inner =>
                            {
                                inner.Item().Text("RouteSync")
                                    .Bold().FontSize(16).FontColor(accentColor);
                                inner.Item().Text(reportTitle)
                                    .Bold().FontSize(11).FontColor("#2D3748");
                            });
                            row.ConstantItem(220).AlignRight().Column(inner =>
                            {
                                inner.Item().Text("Operational Day")
                                    .Bold().FontSize(8).FontColor("#9AA5B4").LetterSpacing(0.06f);
                                inner.Item().Text(OpDayLabel(anchor))
                                    .Bold().FontSize(9.5f).FontColor("#2D3748");
                                inner.Item().Text($"Generated: {PhClock.Now:MMM dd, yyyy hh:mm tt}")
                                    .FontSize(8).FontColor("#9AA5B4");
                            });
                        });

                        col.Item().PaddingTop(6).LineHorizontal(1.5f)
                            .LineColor(accentColor);
                    });

                    // ── Content ───────────────────────────────────────
                    page.Content().PaddingTop(12).Column(col =>
                    {
                        if (groups.Count == 0)
                        {
                            col.Item().AlignCenter().PaddingTop(40)
                                .Text("No data found for the selected filters.")
                                .FontColor("#9AA5B4").FontSize(10);
                            return;
                        }

                        foreach (var group in groups)
                        {
                            // Group label
                            col.Item().PaddingBottom(4).Text(group.GroupName)
                                .Bold().FontSize(9).FontColor("#9AA5B4")
                                .LetterSpacing(0.06f);

                            // Table
                            col.Item().PaddingBottom(14).Table(table =>
                            {
                                // Column definitions
                                table.ColumnsDefinition(def =>
                                {
                                    foreach (var _ in columns)
                                        def.RelativeColumn();
                                });

                                // Header row
                                table.Header(header =>
                                {
                                    foreach (var c in columns)
                                    {
                                        header.Cell()
                                            .Background("#F8F9FA")
                                            .BorderBottom(1).BorderColor("#E8ECF0")
                                            .Padding(5)
                                            .Text(c)
                                            .Bold().FontSize(8).FontColor("#9AA5B4");
                                    }
                                });

                                // Data rows
                                bool alt = false;
                                foreach (var rowData in group.Rows)
                                {
                                    foreach (var cell in rowData)
                                    {
                                        table.Cell()
                                            .Background(alt ? "#FAFBFC" : "#FFFFFF")
                                            .BorderBottom(1).BorderColor("#F0F0F0")
                                            .Padding(5)
                                            .Text(cell ?? "")
                                            .FontSize(8.5f);
                                    }
                                    alt = !alt;
                                }
                            });
                        }

                        // ── Clear grand total at the end ──────────────
                        var totalTrips = filtered.Count;
                        var totalPax = filtered.Sum(Passengers);
                        var totalRev = filtered.Sum(t => t.EstimatedRevenue);
                        string totalsText = reportType switch
                        {
                            "Passenger" => $"Total Trips: {totalTrips}        Total Passengers: {totalPax:N0}",
                            "Revenue" => $"Total Trips: {totalTrips}        Total Revenue: ₱{totalRev:N2}",
                            _ => $"Total Trips: {totalTrips}        Total Passengers: {totalPax:N0}        Total Revenue: ₱{totalRev:N2}",
                        };
                        col.Item().PaddingTop(6).BorderTop(1.5f).BorderColor(accentColor)
                            .PaddingTop(8).Row(row =>
                            {
                                row.RelativeItem().Text("TOTAL")
                                    .Bold().FontSize(10).FontColor(accentColor).LetterSpacing(0.06f);
                                row.RelativeItem().AlignRight().Text(totalsText)
                                    .Bold().FontSize(10).FontColor("#2D3748");
                            });
                    });

                    // ── Footer ────────────────────────────────────────
                    page.Footer().AlignCenter()
                        .Text(text =>
                        {
                            text.Span("RouteSync  •  ").FontColor("#9AA5B4").FontSize(8);
                            text.Span(reportTitle).FontColor("#9AA5B4").FontSize(8);
                            text.Span("  •  Page ").FontColor("#9AA5B4").FontSize(8);
                            text.CurrentPageNumber().FontColor("#9AA5B4").FontSize(8);
                            text.Span(" of ").FontColor("#9AA5B4").FontSize(8);
                            text.TotalPages().FontColor("#9AA5B4").FontSize(8);
                        });
                });
            }).GeneratePdf();

            return File(pdfBytes, "application/pdf", fileName);
        }

        // ── Download report as CSV ────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> DownloadReportCsv(
            string reportType,
            DateTime? date,
            int? routeId,
            int? driverId,
            string? vehicleId)
        {
            // Default to the current operating cycle (06:00->05:59 next day), not raw
            // calendar day — before 6 AM we're still inside yesterday's service day.
            var anchor = (date ?? PhClock.OperationalDay).Date;

            var routesResp = await _supabase.From<BusRoute>().Get();
            var routeNames = routesResp.Models.ToDictionary(r => r.RouteId, r => r.RouteName);

            var usersResp = await _supabase.From<UserModel>().Get();
            var userNames = usersResp.Models.ToDictionary(u => u.UserId, u => $"{u.FirstName} {u.LastName}");

            var vehiclesResp = await _supabase.From<Vehicle>().Get();
            var vehiclesById = vehiclesResp.Models.ToDictionary(v => v.VehicleId, v => v);

            var tripsResp = await _supabase.From<Trip>().Get();

            int Passengers(Trip t) => t.TotalBoarded;

            var filtered = tripsResp.Models
                .Where(t => t.Date.Date == anchor)
                .Where(t => !routeId.HasValue || t.RouteId == routeId.Value)
                .Where(t => !driverId.HasValue || t.DriverId == driverId.Value)
                .Where(t => string.IsNullOrEmpty(vehicleId) || t.VehicleId == vehicleId)
                .OrderBy(t => t.RouteId)
                .ThenBy(t => t.ShiftStartTime)
                .ToList();

            string CsvEscape(string s) =>
                s != null && (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
                    ? $"\"{s.Replace("\"", "\"\"")}\""
                    : (s ?? "");

            var sb = new System.Text.StringBuilder();
            string fileName;

            // Banner: brand + the operational day this report covers, then a blank line.
            var reportLabel = reportType switch
            {
                "Passenger" => "Passenger Report",
                "Revenue" => "Revenue Report",
                _ => "Daily Trip Report"
            };
            sb.AppendLine("RouteSync - " + reportLabel);
            sb.AppendLine("Operational Day," + CsvEscape(OpDayLabel(anchor)));
            sb.AppendLine();

            switch (reportType)
            {
                case "Passenger":
                    fileName = $"PassengerReport_{anchor:yyyy-MM-dd}.csv";
                    sb.AppendLine("Trip ID,Date,Driver,Route,Shift,Shift Time,Passengers");
                    foreach (var t in filtered)
                        sb.AppendLine(string.Join(",",
                            CsvEscape(t.TripId),
                            CsvEscape(t.Date.ToString("MMM dd, yyyy")),
                            CsvEscape(userNames.TryGetValue(t.DriverId, out var dn) ? dn : "N/A"),
                            CsvEscape(routeNames.TryGetValue(t.RouteId, out var rn) ? rn : "N/A"),
                            CsvEscape(t.ShiftType ?? ""),
                            CsvEscape(ShiftRange(t, "-")),
                            Passengers(t).ToString()));
                    break;

                case "Revenue":
                    fileName = $"RevenueReport_{anchor:yyyy-MM-dd}.csv";
                    sb.AppendLine("Trip ID,Date,Driver,Bus ID,Route,Shift,Estimated Revenue");
                    foreach (var t in filtered)
                        sb.AppendLine(string.Join(",",
                            CsvEscape(t.TripId),
                            CsvEscape(t.Date.ToString("MMM dd, yyyy")),
                            CsvEscape(userNames.TryGetValue(t.DriverId, out var dn) ? dn : "N/A"),
                            CsvEscape(vehiclesById.TryGetValue(t.VehicleId, out var v) ? v.PlateNumber : t.VehicleId),
                            CsvEscape(routeNames.TryGetValue(t.RouteId, out var rn) ? rn : "N/A"),
                            CsvEscape(t.ShiftType ?? ""),
                            t.EstimatedRevenue.ToString("F2")));
                    break;

                default:
                    fileName = $"DailyTripReport_{anchor:yyyy-MM-dd}.csv";
                    sb.AppendLine("Trip ID,Date,Driver,Bus ID,Route,Shift,Shift Time,Actual Start,Actual End,Passengers,Revenue");
                    foreach (var t in filtered)
                        sb.AppendLine(string.Join(",",
                            CsvEscape(t.TripId),
                            CsvEscape(t.Date.ToString("MMM dd, yyyy")),
                            CsvEscape(userNames.TryGetValue(t.DriverId, out var dn) ? dn : "N/A"),
                            CsvEscape(vehiclesById.TryGetValue(t.VehicleId, out var v) ? v.PlateNumber : t.VehicleId),
                            CsvEscape(routeNames.TryGetValue(t.RouteId, out var rn) ? rn : "N/A"),
                            CsvEscape(t.ShiftType ?? ""),
                            CsvEscape(ShiftRange(t, "-")),
                            CsvEscape(FmtActual(t.ActualStartTime)),
                            CsvEscape(FmtActual(t.ActualEndTime)),
                            Passengers(t).ToString(),
                            t.EstimatedRevenue.ToString("F2")));
                    break;
            }

            // ── Clear grand total at the end ──────────────────────────
            sb.AppendLine();
            sb.AppendLine("TOTAL");
            sb.AppendLine($"Total Trips,{filtered.Count}");
            if (reportType != "Revenue")
                sb.AppendLine($"Total Passengers,{filtered.Sum(Passengers)}");
            if (reportType != "Passenger")
                sb.AppendLine($"Total Revenue,{filtered.Sum(t => t.EstimatedRevenue):F2}");

            var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
            return File(bytes, "text/csv", fileName);
        }

        // ── Shift-time helpers ───────────────────────────────────────
        // Trips are dated their START day. A shift whose end <= start is overnight and
        // actually ends the NEXT calendar day, so build the times off the trip's real date
        // (not PhClock.Today) and roll the end +1 day when overnight.
        private static bool IsOvernight(Trip t) => t.ShiftEndTime <= t.ShiftStartTime;
        private static DateTime ShiftStartAt(Trip t) => t.Date.Date + t.ShiftStartTime;
        private static DateTime ShiftEndAt(Trip t) => t.Date.Date + t.ShiftEndTime
            + (IsOvernight(t) ? TimeSpan.FromDays(1) : TimeSpan.Zero);

        // Plain clock end time, e.g. "06:00 AM" — no date/"+1" suffix (the operational-day
        // header at the top of the report already makes the service window clear).
        private static string ShiftEndLabel(Trip t) => ShiftEndAt(t).ToString("hh:mm tt");

        // Full window: "10:00 PM – 06:00 AM".
        private static string ShiftRange(Trip t, string dash = "–") =>
            $"{ShiftStartAt(t):hh:mm tt} {dash} {ShiftEndLabel(t)}";

        // Actual logged start/end. Stored timestamptz comes back LOCAL-kind (+8h shifted),
        // so normalize to UTC to recover the true PH wall-clock digits. "—" when not logged.
        private static string FmtActual(DateTime? dt) =>
            dt.HasValue ? dt.Value.ToUniversalTime().ToString("hh:mm tt") : "—";

        // Operational-day banner: "June 18, 2026  •  6:00 AM – June 19, 5:59 AM".
        private static string OpDayLabel(DateTime anchor) =>
            $"{anchor:MMMM d, yyyy}  •  6:00 AM – {anchor.AddDays(1):MMMM d}, 5:59 AM";

        // ── Internal model for report groups ─────────────────────────
        private class ReportGroup
        {
            public string GroupName { get; set; } = "";
            public List<string[]> Rows { get; set; } = new();
        }
    }
}
