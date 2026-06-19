using FleetWise.Models;
using FleetWise.Services;
using FleetWise.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using static Postgrest.Constants;

namespace FleetWise.Controllers
{
    [Authorize]
    public class ScheduleController : Controller
    {
        private readonly Supabase.Client _supabase;
        public ScheduleController(Supabase.Client supabase) => _supabase = supabase;

        // Fixed shift windows (mirrors the Add Trip modal's SHIFT_TIMES).
        private static readonly Dictionary<string, (TimeSpan start, TimeSpan end)> ShiftTimes = new()
        {
            ["Morning"] = (new(6, 0, 0), new(14, 0, 0)),
            ["Afternoon"] = (new(14, 0, 0), new(22, 0, 0)),
            ["Evening"] = (new(22, 0, 0), new(6, 0, 0)),
        };
        private static readonly string[] ShiftOrder = { "Morning", "Afternoon", "Evening" };

        // ── GET weekly planner ────────────────────────────────────────
        public async Task<IActionResult> Index(string start)
        {
            // Week = picked start date .. start+6. Default = today.
            var weekStart = (DateTime.TryParse(start, out var s) ? s : PhClock.Today).Date;
            var weekEnd = weekStart.AddDays(6);

            var routesTask = _supabase.From<BusRoute>().Get();
            var vehiclesTask = _supabase.From<Vehicle>().Get();
            var driversTask = _supabase.From<UserModel>()
                                .Filter("role_id", Operator.Equals, "2")
                                .Filter("account_status", Operator.Equals, "Activated")
                                .Get();
            var tripsTask = _supabase.From<Trip>()
                                .Filter("date", Operator.GreaterThanOrEqual, weekStart.ToString("yyyy-MM-dd"))
                                .Filter("date", Operator.LessThanOrEqual, weekEnd.ToString("yyyy-MM-dd"))
                                .Get();

            await Task.WhenAll(routesTask, vehiclesTask, driversTask, tripsTask);

            var vm = new ScheduleViewModel
            {
                WeekStart = weekStart,
                WeekEnd = weekEnd,
                Days = Enumerable.Range(0, 7).Select(i => weekStart.AddDays(i)).ToList(),
                PrevWeekStart = weekStart.AddDays(-7).ToString("yyyy-MM-dd"),
                NextWeekStart = weekStart.AddDays(7).ToString("yyyy-MM-dd"),
                Routes = routesTask.Result.Models.OrderBy(r => r.RouteId)
                    .Select(r => new RouteOption { RouteId = r.RouteId, RouteName = r.RouteName }).ToList(),
                Vehicles = vehiclesTask.Result.Models
                    // Flagged buses stay schedulable (advisory) — only grounded
                    // (out-of-service) buses are withheld.
                    .Where(v => !v.OutOfService)
                    .OrderBy(v => v.VehicleId)
                    .Select(v => new VehicleOption { VehicleId = v.VehicleId, PlateNumber = v.PlateNumber }).ToList(),
                Drivers = driversTask.Result.Models.OrderBy(d => d.FirstName)
                    .Select(d => new DriverOption { DriverId = d.UserId, DriverName = $"{d.FirstName} {d.LastName}" }).ToList(),
            };

            foreach (var t in tripsTask.Result.Models.OrderBy(t => t.VehicleId))
            {
                var key = $"{t.RouteId}|{t.ShiftType}|{t.Date:yyyy-MM-dd}";
                if (!vm.Cells.TryGetValue(key, out var list))
                    vm.Cells[key] = list = new List<ScheduleCell>();
                list.Add(new ScheduleCell
                {
                    TripId = t.TripId,
                    VehicleId = t.VehicleId,
                    DriverId = t.DriverId,
                    TripStatus = t.TripStatus
                });
            }

            return View(vm);
        }

        // ── POST bulk save ────────────────────────────────────────────
        [HttpPost]
        public async Task<IActionResult> Save([FromBody] SaveScheduleRequest req)
        {
            if (req == null || !DateTime.TryParse(req.WeekStart, out var weekStart)
                            || !DateTime.TryParse(req.WeekEnd, out var weekEnd))
                return BadRequest("Invalid week range.");

            var cells = req.Cells ?? new();

            // Existing trips in the range (also needed to validate against locked trips the
            // grid may not resend).
            var existingResp = await _supabase.From<Trip>()
                .Filter("date", Operator.GreaterThanOrEqual, weekStart.ToString("yyyy-MM-dd"))
                .Filter("date", Operator.LessThanOrEqual, weekEnd.ToString("yyyy-MM-dd"))
                .Get();
            var existing = existingResp.Models;
            var existingById = existing.ToDictionary(t => t.TripId);

            // ── Conflict validation across the EFFECTIVE schedule ─────
            // Submitted (editable) cells PLUS any existing locked (Active/Completed) trip the
            // grid didn't resend — those still occupy that driver/vehicle/shift, so a NEW cell
            // colliding with them is a real double-booking. But a clash between two LOCKED
            // trips is immutable history the dispatcher can't fix, so it must never block the
            // save (that was the false flag). A conflict is reported only when at least one
            // side is editable, and only the editable cells are returned for highlighting.
            var submittedIds = cells.Where(c => !string.IsNullOrEmpty(c.TripId)).Select(c => c.TripId).ToHashSet();
            var effective = cells.Select(c => (cell: c, locked: false)).ToList();
            effective.AddRange(existing
                .Where(t => !submittedIds.Contains(t.TripId)
                         && (t.TripStatus == "Active" || t.TripStatus == "Completed"))
                .Select(t => (cell: new ScheduleCellInput
                {
                    TripId = t.TripId,
                    VehicleId = t.VehicleId,
                    DriverId = t.DriverId,
                    Shift = t.ShiftType,
                    RouteId = t.RouteId,
                    Date = t.Date.ToString("yyyy-MM-dd"),
                }, locked: true)));

            var conflicts = FindConflicts(effective);
            if (conflicts.Count > 0) return BadRequest(new { conflicts });

            // Trip ids that survive this save (existing rows kept/updated).
            var keptIds = new HashSet<string>();

            foreach (var c in cells)
            {
                if (string.IsNullOrEmpty(c.VehicleId) || c.DriverId == 0
                 || string.IsNullOrEmpty(c.Shift) || !DateTime.TryParse(c.Date, out var date))
                    continue;
                if (!ShiftTimes.TryGetValue(c.Shift, out var window)) continue;

                if (!string.IsNullOrEmpty(c.TripId) && existingById.TryGetValue(c.TripId, out var trip))
                {
                    keptIds.Add(trip.TripId);
                    // Locked once the trip has started — never touch it.
                    if (trip.TripStatus == "Active" || trip.TripStatus == "Completed") continue;
                    if (trip.VehicleId == c.VehicleId && trip.DriverId == c.DriverId) continue; // unchanged

                    await _supabase.From<Trip>()
                        .Filter("trip_id", Operator.Equals, trip.TripId)
                        .Set(t => t.VehicleId, c.VehicleId)
                        .Set(t => t.DriverId, c.DriverId)
                        .Update();
                }
                else
                {
                    // New bus on this route/shift/day.
                    await _supabase.From<Trip>().Insert(new Trip
                    {
                        Date = DateTime.SpecifyKind(date, DateTimeKind.Utc),
                        ShiftType = c.Shift,
                        ShiftStartTime = window.start,
                        ShiftEndTime = window.end,
                        RouteId = c.RouteId,
                        VehicleId = c.VehicleId,
                        DriverId = c.DriverId,
                        TripStatus = "Not Yet Started",
                        EstimatedRevenue = 0
                    });
                }
            }

            // Removed lanes -> delete trips no longer present (skip started ones)
            foreach (var t in existing)
            {
                if (keptIds.Contains(t.TripId)) continue;
                if (t.TripStatus == "Active" || t.TripStatus == "Completed") continue;

                await _supabase.From<Trip>()
                    .Filter("trip_id", Operator.Equals, t.TripId)
                    .Delete();
            }

            return Ok();
        }

        // Conflicts in the effective schedule. A conflict is reported ONLY when at least one
        // participating cell is editable (locked == false); two locked trips clashing is
        // immutable history and must never block the save. Each conflict carries the editable
        // cells involved so the UI can highlight exactly what the dispatcher must fix.
        private static List<ConflictDto> FindConflicts(List<(ScheduleCellInput cell, bool locked)> all)
        {
            var results = new List<ConflictDto>();

            var valid = all.Where(x => !string.IsNullOrEmpty(x.cell.VehicleId) && x.cell.DriverId != 0
                                    && !string.IsNullOrEmpty(x.cell.Shift) && DateTime.TryParse(x.cell.Date, out _))
                           .ToList();

            static CellRef Ref((ScheduleCellInput cell, bool locked) x) =>
                new() { RouteId = x.cell.RouteId, Shift = x.cell.Shift, Date = x.cell.Date };

            // ── Per (day, shift): a driver or vehicle booked twice ──
            foreach (var g in valid.GroupBy(x => new { x.cell.Date, x.cell.Shift }))
            {
                foreach (var dup in g.GroupBy(x => x.cell.DriverId).Where(x => x.Count() > 1))
                {
                    var editable = dup.Where(x => !x.locked).ToList();
                    if (editable.Count == 0) continue; // locked-vs-locked -> not the user's problem
                    results.Add(new ConflictDto
                    {
                        Message = $"This driver is already booked for the {g.Key.Shift} shift on {FmtDate(g.Key.Date)}.",
                        Cells = editable.Select(Ref).ToList()
                    });
                }

                foreach (var dup in g.GroupBy(x => x.cell.VehicleId).Where(x => x.Count() > 1))
                {
                    var editable = dup.Where(x => !x.locked).ToList();
                    if (editable.Count == 0) continue;
                    results.Add(new ConflictDto
                    {
                        Message = $"This bus is already booked for the {g.Key.Shift} shift on {FmtDate(g.Key.Date)}.",
                        Cells = editable.Select(Ref).ToList()
                    });
                }
            }

            // ── Per driver: no two CONSECUTIVE shifts (same-day adjacency or evening->next morning) ──
            foreach (var g in valid.GroupBy(x => x.cell.DriverId))
            {
                var byDayShift = g.GroupBy(x => (Day: DateTime.Parse(x.cell.Date).Date, x.cell.Shift))
                                  .ToDictionary(k => k.Key, k => k.ToList());

                void Pair((DateTime Day, string Shift) a, (DateTime Day, string Shift) b, string message)
                {
                    if (!byDayShift.TryGetValue(a, out var la) || !byDayShift.TryGetValue(b, out var lb)) return;
                    var editable = la.Concat(lb).Where(x => !x.locked).ToList();
                    if (editable.Count == 0) return; // both shifts locked -> immutable, skip
                    results.Add(new ConflictDto { Message = message, Cells = editable.Select(Ref).ToList() });
                }

                foreach (var day in byDayShift.Keys.Select(k => k.Day).Distinct())
                {
                    Pair((day, "Morning"), (day, "Afternoon"),
                        $"This driver is booked for Morning and Afternoon back to back on {FmtDate(day.ToString("yyyy-MM-dd"))}. Give them a break.");
                    Pair((day, "Afternoon"), (day, "Evening"),
                        $"This driver is booked for Afternoon and Evening back to back on {FmtDate(day.ToString("yyyy-MM-dd"))}. Give them a break.");
                    Pair((day, "Evening"), (day.AddDays(1), "Morning"),
                        $"This driver ends with Evening on {FmtDate(day.ToString("yyyy-MM-dd"))} and starts Morning the next day. They need rest.");
                }
            }

            return results;
        }

        private static string FmtDate(string isoDate) =>
            DateTime.TryParse(isoDate, out var d) ? d.ToString("MMM d") : isoDate;

        private sealed class ConflictDto
        {
            public string Message { get; set; } = "";
            public List<CellRef> Cells { get; set; } = new();
        }

        private sealed class CellRef
        {
            public int RouteId { get; set; }
            public string Shift { get; set; } = "";
            public string Date { get; set; } = "";
        }
    }
}
