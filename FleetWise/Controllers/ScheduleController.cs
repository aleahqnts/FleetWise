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
            // Submitted cells PLUS any existing locked (Active/Completed) trip the grid didn't
            // resend — those still occupy that driver/vehicle/shift, so a new cell colliding
            // with them is a double-booking and must be rejected (the bug: a completed
            // Afternoon let an Evening get booked for the same driver back-to-back).
            var submittedIds = cells.Where(c => !string.IsNullOrEmpty(c.TripId)).Select(c => c.TripId).ToHashSet();
            var effective = new List<ScheduleCellInput>(cells);
            effective.AddRange(existing
                .Where(t => !submittedIds.Contains(t.TripId)
                         && (t.TripStatus == "Active" || t.TripStatus == "Completed"))
                .Select(t => new ScheduleCellInput
                {
                    TripId = t.TripId,
                    VehicleId = t.VehicleId,
                    DriverId = t.DriverId,
                    Shift = t.ShiftType,
                    RouteId = t.RouteId,
                    Date = t.Date.ToString("yyyy-MM-dd"),
                }));

            var conflict = ValidateConflicts(effective);
            if (conflict != null) return BadRequest(conflict);

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

        // Returns an error string if a conflict exists, else null.
        private static string ValidateConflicts(List<ScheduleCellInput> cells)
        {
            var valid = cells.Where(c => !string.IsNullOrEmpty(c.VehicleId) && c.DriverId != 0
                                      && !string.IsNullOrEmpty(c.Shift) && DateTime.TryParse(c.Date, out _)).ToList();

            // Per (day, shift): no driver or vehicle assigned twice.
            foreach (var g in valid.GroupBy(c => new { c.Date, c.Shift }))
            {
                var dupDriver = g.GroupBy(c => c.DriverId).FirstOrDefault(x => x.Count() > 1);
                if (dupDriver != null)
                    return $"Driver assigned to two routes in the same {g.Key.Shift} shift on {g.Key.Date}.";

                var dupVeh = g.GroupBy(c => c.VehicleId).FirstOrDefault(x => x.Count() > 1);
                if (dupVeh != null)
                    return $"Vehicle assigned to two routes in the same {g.Key.Shift} shift on {g.Key.Date}.";
            }

            // No driver in two CONSECUTIVE shifts (same day adjacency, or evening -> next-day morning).
            foreach (var g in valid.GroupBy(c => c.DriverId))
            {
                var byDate = g.GroupBy(c => DateTime.Parse(c.Date).Date)
                              .ToDictionary(x => x.Key, x => x.Select(c => c.Shift).ToHashSet());

                foreach (var (day, shifts) in byDate)
                {
                    if (shifts.Contains("Morning") && shifts.Contains("Afternoon"))
                        return $"Driver assigned to back-to-back Morning + Afternoon on {day:yyyy-MM-dd}.";
                    if (shifts.Contains("Afternoon") && shifts.Contains("Evening"))
                        return $"Driver assigned to back-to-back Afternoon + Evening on {day:yyyy-MM-dd}.";

                    // Evening today -> Morning tomorrow
                    if (shifts.Contains("Evening") && byDate.TryGetValue(day.AddDays(1), out var next)
                        && next.Contains("Morning"))
                        return $"Driver works Evening on {day:yyyy-MM-dd} then Morning the next day — no rest.";
                }
            }

            return null;
        }
    }
}
