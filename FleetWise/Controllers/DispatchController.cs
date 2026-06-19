using FleetWise.Models;
using FleetWise.Services;
using FleetWise.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using static Postgrest.Constants;


namespace FleetWise.Controllers
{
    [Authorize]
    public class DispatchController : Controller
    {
        private readonly Supabase.Client _supabase;

        public DispatchController(Supabase.Client supabase)
        {
            _supabase = supabase;
        }

        public async Task<IActionResult> Index(string date)
        {
            // --- Scope dispatch to ONE operational day (06:00 -> 05:59 next morning).
            //     Default = the current service cycle (before 6 AM that's still
            //     yesterday's day). Arrows in the header move this back/forward a day. ---
            var selected = DateTime.TryParse(date, out var d) ? d.Date : PhClock.OperationalDay;
            var selStr = selected.ToString("yyyy-MM-dd");

            // --- Fetch all data in parallel ---
            // A trip is dated its START day. An overnight shift (10pm->6am) rolls past
            // midnight but still belongs to its start day's cycle, so the board for day D
            // is EXACTLY the trips dated D — no previous-day merge (that double-showed an
            // overnight trip on the next day's board too).
            var tripsTask = _supabase.From<Trip>()
                                       .Filter("date", Operator.Equals, selStr)
                                       .Get();
            var vehiclesTask = _supabase.From<Vehicle>().Get();
            var routesTask = _supabase.From<BusRoute>().Get();
            var driversTask = _supabase.From<UserModel>()
                                       .Filter("role_id", Operator.Equals, "2")
                                       .Filter("account_status", Operator.Equals, "Activated")
                                       .Get();
            var availabilityTask = _supabase.From<DriverAvailability>().Get();
            var checklistsTask = _supabase.From<BusChecklist>().Get();
            var maintTask = _supabase.From<MaintenanceLog>().Get();

            await Task.WhenAll(tripsTask, vehiclesTask, routesTask, driversTask, availabilityTask, checklistsTask, maintTask);

            // Trips for this operational day (overnight ones included — they're dated today).
            var trips = tripsTask.Result.Models
                .Where(t => t.Date.Date == selected)
                .ToList();
            var vehicles = vehiclesTask.Result.Models;
            var routes = routesTask.Result.Models;
            var drivers = driversTask.Result.Models;
            var availability = availabilityTask.Result.Models;
            var checklists = checklistsTask.Result.Models;

            // Vehicles with an OPEN incident (unresolved maintenance_log) are flagged
            // PERSISTENTLY — independent of any trip — so the flag survives the bus going
            // On Trip and outlives the volatile vehicle_status column.
            var flaggedVehicleIds = maintTask.Result.Models
                .Where(l => l.ResolvedAt == null && l.VehicleId != null)
                .Select(l => l.VehicleId)
                .ToHashSet();

            // --- Build lookup dictionaries ---
            var vehicleDict = vehicles.ToDictionary(v => v.VehicleId);
            var driverDict = drivers.ToDictionary(d => d.UserId);
            var availabilityDict = availability.ToDictionary(a => a.UserId, a => a.AvailabilityStatus);

            // One checklist per trip — if a bus was re-inspected, use the latest submission.
            var checklistDict = checklists
                .GroupBy(c => c.TripId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.SubmittedAt).First());

            // --- Resolve vehicle/driver/trip status once per trip, derived from the
            //     inspection log so it always agrees with the Trip Detail modal:
            //       - no checklist submitted        -> vehicle Pending
            //       - checklist status == "Passed"  -> vehicle Ready to Deploy
            //       - anything else (e.g. "Failed") -> vehicle Flagged
            //     Match is case-insensitive, mirroring the JS check in the modal.
            (Vehicle Vehicle, UserModel Driver, string VehicleStatus, string DriverStatus, string TripStatus, bool Flagged) Resolve(Trip trip)
            {
                vehicleDict.TryGetValue(trip.VehicleId, out var vehicle);
                driverDict.TryGetValue(trip.DriverId, out var driver);
                var driverAvail = availabilityDict.TryGetValue(trip.DriverId, out var avail) ? avail : "Available";

                // Roadworthiness comes from THIS trip's own inspection, never the shared
                // vehicle_status column (which a later shift's start/end/checklist overwrites
                // -> stale green on a missed trip). A submitted checklist that isn't "Passed"
                // is a flag, and it's reported independently of the operational dot.
                var cl = checklistDict.TryGetValue(trip.TripId, out var c0) ? c0 : null;
                bool flagged = (cl != null && !string.Equals(cl.ChecklistStatus, "Passed", StringComparison.OrdinalIgnoreCase))
                               || flaggedVehicleIds.Contains(trip.VehicleId);

                if (trip.TripStatus == "Active")
                    return (vehicle, driver, "On Trip", "On Trip", "Active", flagged);

                if (trip.TripStatus == "Completed")
                    return (vehicle, driver, "Completed", "Available", "Completed", flagged);

                // Waiting to depart — readiness is derived from the trip's checklist, not the
                // bus row: no checklist yet -> Pending; failed -> Flagged; passed -> Ready.
                var vehicleStatus = cl == null ? "Pending" : flagged ? "Flagged" : "Ready to Deploy";

                // Treat null/missing availability as Available
                var driverStatus = driver == null
                    ? "Unavailable"
                    : string.IsNullOrEmpty(driverAvail) ? "Available" : driverAvail;

                // Shift already ended but the trip never went Active/Completed -> it was
                // MISSED. Time-relative (derived per request, not stored), so a past
                // operational day shows missed trips instead of a stale "Not Yet Started".
                // A flag is advisory now — the bus is still deployable. Only a grounded
                // (out-of-service) bus or an unavailable driver is a blocking Assignment Issue.
                var tripStatus = ShiftEndAt(trip) < PhClock.Now
                    ? "Missed"
                    : (vehicle?.OutOfService == true || driverStatus == "Unavailable")
                        ? "Assignment Issue"
                        : vehicleStatus == "Pending"
                            ? "Pending"
                            : "Not Yet Started";

                return (vehicle, driver, vehicleStatus, driverStatus, tripStatus, flagged);
            }

            var resolved = new Dictionary<string, (Vehicle Vehicle, UserModel Driver, string VehicleStatus, string DriverStatus, string TripStatus, bool Flagged)>();
            foreach (var trip in trips)
            {
                try { resolved[trip.TripId] = Resolve(trip); }
                catch { resolved[trip.TripId] = (null, null, "Pending", "Available", "Pending", false); }
            }

            // --- Stats ---
            int activeTrips = trips.Count(t => resolved[t.TripId].TripStatus == "Active");
            // Still awaiting departure = not started, not finished, and NOT already missed
            // (Not Yet Started + Pending checklist + Assignment Issue). Missed trips are
            // past their window so they no longer count as awaiting.
            int notStarted = trips.Count(t =>
                resolved[t.TripId].TripStatus != "Active"
                && resolved[t.TripId].TripStatus != "Completed"
                && resolved[t.TripId].TripStatus != "Missed");
            int unassigned = trips.Count(t => resolved[t.TripId].TripStatus == "Assignment Issue");
            int flaggedVehicles = vehicles.Count(v => flaggedVehicleIds.Contains(v.VehicleId));
            int unavailableDrivers = availability.Count(a => a.AvailabilityStatus == "Unavailable");

            // --- Group trips by route → shift ---
            var vm = new DispatchViewModel
            {
                ScheduleDate = selected,
                PrevDate = selected.AddDays(-1).ToString("yyyy-MM-dd"),
                NextDate = selected.AddDays(1).ToString("yyyy-MM-dd"),
                IsToday = selected == PhClock.OperationalDay,
                ActiveTrips = activeTrips,
                TripsNotStarted = notStarted,
                UnassignedTrips = unassigned,
                FlaggedVehicles = flaggedVehicles,
                UnavailableDrivers = unavailableDrivers
            };

            foreach (var route in routes.OrderBy(r => r.RouteId))
            {
                var routeTrips = trips.Where(t => t.RouteId == route.RouteId).ToList();

                var routeGroup = new RouteDispatchGroup
                {
                    RouteId = route.RouteId,
                    RouteName = route.RouteName,
                    NeedsAssignment = routeTrips.Any(t => resolved[t.TripId].TripStatus == "Assignment Issue")
                };

                if (!routeTrips.Any())
                {
                    // No trips scheduled for this route today — leave Shifts empty;
                    // the view will render an empty state for this route card.
                    vm.Routes.Add(routeGroup);
                    continue;
                }

                // Group by shift
                var shiftGroups = routeTrips
                    .GroupBy(t => new { t.ShiftType, t.ShiftStartTime, t.ShiftEndTime })
                    .OrderBy(g => g.Key.ShiftStartTime);

                foreach (var shiftGroup in shiftGroups)
                {
                    // Build the shift window on the SELECTED operational day so an overnight
                    // shift's end correctly lands on the next morning; flag it so the view can
                    // show a "+1" day hint and never look like it ends the same morning.
                    var startTs = shiftGroup.Key.ShiftStartTime;
                    var endTs = shiftGroup.Key.ShiftEndTime;
                    bool overnight = endTs <= startTs;
                    var startDt = selected.Add(startTs);
                    var endDt = selected.Add(endTs).AddDays(overnight ? 1 : 0);

                    var shift = new ShiftGroup
                    {
                        ShiftType = shiftGroup.Key.ShiftType,
                        ShiftStartTime = startDt.ToString("h:mm tt"),
                        ShiftEndTime = endDt.ToString("h:mm tt"),
                        IsOvernight = overnight
                    };

                    foreach (var trip in shiftGroup.OrderBy(t => t.VehicleId))
                    {
                        var r = resolved[trip.TripId];

                        shift.Trips.Add(new TripRow
                        {
                            TripId = trip.TripId,
                            VehicleId = trip.VehicleId,
                            PlateNumber = r.Vehicle?.PlateNumber ?? "—",
                            VehicleStatus = r.VehicleStatus,
                            DriverName = r.Driver != null
                                ? $"{r.Driver.FirstName} {r.Driver.LastName}"
                                : "Unassigned",
                            DriverStatus = r.DriverStatus,
                            TripStatus = r.TripStatus,
                            Flagged = r.Flagged
                        });
                    }

                    routeGroup.Shifts.Add(shift);
                }

                vm.Routes.Add(routeGroup);
            }

            return View(vm);
        }

        [HttpGet]
        public async Task<IActionResult> GetTripDetails(string id)
        {
            if (string.IsNullOrEmpty(id))
                return BadRequest("Trip ID is required.");

            var tripResult = await _supabase.From<Trip>()
                .Filter("trip_id", Operator.Equals, id)
                .Get();
            var trip = tripResult.Models.FirstOrDefault();

            if (trip == null)
                return NotFound();

            var vehicleTask = _supabase.From<Vehicle>()
                                    .Filter("vehicle_id", Operator.Equals, trip.VehicleId).Get();
            var driverTask = _supabase.From<UserModel>()
                                    .Filter("user_id", Operator.Equals, trip.DriverId.ToString()).Get();
            var routeTask = _supabase.From<BusRoute>()
                                    .Filter("route_id", Operator.Equals, trip.RouteId.ToString()).Get();
            var availabilityTask = _supabase.From<DriverAvailability>()
                                    .Filter("user_id", Operator.Equals, trip.DriverId.ToString()).Get();
            var checklistTask = _supabase.From<BusChecklist>()
                                    .Filter("trip_id", Operator.Equals, id).Get();

            await Task.WhenAll(vehicleTask, driverTask, routeTask, availabilityTask, checklistTask);

            var vehicle = vehicleTask.Result.Models.FirstOrDefault();
            var driver = driverTask.Result.Models.FirstOrDefault();
            var route = routeTask.Result.Models.FirstOrDefault();
            var availability = availabilityTask.Result.Models.FirstOrDefault();
            var checklist = checklistTask.Result.Models.FirstOrDefault();

            // Resolve display statuses
            string vehicleStatus, driverStatus, resolvedTripStatus;
            if (trip.TripStatus == "Active")
            {
                vehicleStatus = "On Trip";
                driverStatus = "On Trip";
                resolvedTripStatus = "Active";
            }
            else if (trip.TripStatus == "Completed")
            {
                vehicleStatus = "Trip Completed";
                driverStatus = "Trip Completed";
                resolvedTripStatus = "Completed";
            }
            else
            {
                // Vehicle status is derived from THIS trip's inspection log itself (not the
                // shared vehicle_status column, which a later shift overwrites), so
                // "Vehicle Details" always agrees with the Inspection Log card:
                //   - no checklist submitted yet     -> Pending
                //   - checklist status == "Passed"   -> Ready to Deploy
                //   - anything else (e.g. "Failed")  -> Flagged
                // Match is case-insensitive, mirroring the JS check in the modal.
                var clFailed = checklist != null && !string.Equals(checklist.ChecklistStatus, "Passed", StringComparison.OrdinalIgnoreCase);
                vehicleStatus = checklist == null ? "Pending" : clFailed ? "Flagged" : "Ready to Deploy";

                driverStatus = driver == null
                    ? "Unavailable"
                    : (availability?.AvailabilityStatus ?? "Available");

                // Overall trip status mirrors the dispatch dashboard's resolution,
                // so the header badge always agrees with Vehicle Status / Inspection Log.
                // Past its window without running -> Missed.
                resolvedTripStatus = ShiftEndAt(trip) < PhClock.Now
                    ? "Missed"
                    : (vehicleStatus == "Flagged" || driverStatus == "Unavailable")
                        ? "Assignment Issue"
                        : vehicleStatus == "Pending"
                            ? "Pending"
                            : "Not Yet Started";
            }

            var vm = new TripDetailViewModel
            {
                TripId = trip.TripId,
                TripStatus = resolvedTripStatus,
                ShiftType = trip.ShiftType,
                ShiftStartTime = FormatShiftWindow(trip).Start,
                ShiftEndTime = FormatShiftWindow(trip).End,
                RouteName = route?.RouteName ?? "—",
                VehicleId = trip.VehicleId,
                PlateNumber = vehicle?.PlateNumber ?? "—",
                VehicleStatus = vehicleStatus,
                DriverName = driver != null ? $"{driver.FirstName} {driver.LastName}" : "Unassigned",
                DriverId = trip.DriverId.ToString(),
                DriverStatus = driverStatus,

                IsCompleted = trip.TripStatus == "Completed",
                TotalBoarded = trip.TripStatus == "Completed" ? trip.TotalBoarded : null,
                EstimatedRevenue = trip.TripStatus == "Completed" ? trip.EstimatedRevenue : null,
                // Stored timestamptz digits are already PH wall-clock (PhClock.NowForDb).
                // Postgrest deserializes the "+00:00" value to a LOCAL-kind DateTime, so a
                // plain ToString() shifts it +8h (01:06 -> 09:06). Normalize back to UTC to
                // print the raw stored digits unchanged.
                ActualStartTime = trip.ActualStartTime?.ToUniversalTime().ToString("h:mm tt"),
                ActualEndTime = trip.ActualEndTime?.ToUniversalTime().ToString("h:mm tt"),

                Checklist = checklist != null ? new TripChecklistViewModel
                {
                    ChecklistId = checklist.ChecklistId,
                    SubmittedAt = checklist.SubmittedAt,
                    ChecklistStatus = checklist.ChecklistStatus,
                    Notes = checklist.Notes,
                    ExteriorInspection = checklist.ExteriorInspection ?? new(),
                    EngineCompartment = checklist.EngineCompartment ?? new(),
                    InteriorInspection = checklist.InteriorInspection ?? new(),
                    BrakeSafety = checklist.BrakeSafety ?? new(),
                    PassengerSystems = checklist.PassengerSystems ?? new(),
                } : null
            };

            if (checklist != null)
            {
                var logs = await _supabase.From<MaintenanceLog>()
                    .Filter("checklist_id", Operator.Equals, checklist.ChecklistId.ToString())
                    .Get();

                vm.MaintenanceLogs = logs.Models.Select(l => new TripMaintenanceLogViewModel
                {
                    LogId = l.LogId,
                    IssueDetails = l.IssueDetails?.Issues ?? new(),
                    MaintenanceStatus = l.MaintenanceStatus,
                    CreatedAt = l.CreatedAt,
                    ResolvedAt = l.ResolvedAt,
                    Remarks = l.Remarks
                }).ToList();
            }

            return Json(vm);
        }

        // ── GET options for Add Trip modal ────────────────────────────
        [HttpGet]
        public async Task<IActionResult> GetAddTripOptions()
        {
            var today = PhClock.OperationalDay.ToString("yyyy-MM-dd");

            var tripsTask = _supabase.From<Trip>()
                                        .Filter("date", Operator.Equals, today)
                                        .Get();
            var vehiclesTask = _supabase.From<Vehicle>().Get();
            var routesTask = _supabase.From<BusRoute>().Get();
            var driversTask = _supabase.From<UserModel>()
                                        .Filter("role_id", Operator.Equals, "2")
                                        .Filter("account_status", Operator.Equals, "Activated")
                                        .Get();
            var availTask = _supabase.From<DriverAvailability>().Get();

            await Task.WhenAll(tripsTask, vehiclesTask, routesTask, driversTask, availTask);

            var todayTrips = tripsTask.Result.Models;
            var vehicles = vehiclesTask.Result.Models;
            var routes = routesTask.Result.Models;
            var drivers = driversTask.Result.Models;
            var availability = availTask.Result.Models
                                        .ToDictionary(a => a.UserId, a => a.AvailabilityStatus);

            // Build per-vehicle booked shifts for today
            var vehicleBookedShifts = todayTrips
                .GroupBy(t => t.VehicleId)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(t => t.ShiftType).Distinct().ToList()
                );

            // Build per-driver booked shifts for today
            var driverBookedShifts = todayTrips
                .GroupBy(t => t.DriverId)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(t => t.ShiftType).Distinct().ToList()
                );

            var vm = new AddTripOptionsViewModel
            {
                Routes = routes
                    .OrderBy(r => r.RouteId)
                    .Select(r => new RouteOption
                    {
                        RouteId = r.RouteId,
                        RouteName = r.RouteName
                    }).ToList(),

                // Flagged buses stay deployable (advisory) — only grounded (out-of-service)
                // buses are withheld from assignment.
                Vehicles = vehicles
                    .Where(v => !v.OutOfService)
                    .OrderBy(v => v.VehicleId)
                    .Select(v => new VehicleOption
                    {
                        VehicleId = v.VehicleId,
                        PlateNumber = v.PlateNumber,
                        BookedShifts = vehicleBookedShifts.TryGetValue(v.VehicleId, out var vs)
                                        ? vs : new()
                    }).ToList(),

                // Only drivers who are Available (not Unavailable)
                Drivers = drivers
                    .Where(d => !availability.TryGetValue(d.UserId, out var s) || s != "Unavailable")
                    .OrderBy(d => d.FirstName)
                    .Select(d => new DriverOption
                    {
                        DriverId = d.UserId,
                        DriverName = $"{d.FirstName} {d.LastName}",
                        BookedShifts = driverBookedShifts.TryGetValue(d.UserId, out var ds)
                                        ? ds : new()
                    }).ToList()
            };

            return Json(vm);
        }

        // ── POST create trip ──────────────────────────────────────────
        [HttpPost]
        public async Task<IActionResult> CreateTrip([FromBody] CreateTripRequest req)
        {
            if (req == null
             || string.IsNullOrEmpty(req.ShiftType)
             || string.IsNullOrEmpty(req.VehicleId)
             || req.RouteId == 0
             || req.DriverId == 0)
                return BadRequest("Missing required fields.");

            // Parse shift times
            if (!TimeSpan.TryParse(req.ShiftStartTime, out var startTime)
             || !TimeSpan.TryParse(req.ShiftEndTime, out var endTime))
                return BadRequest("Invalid shift times.");

            var conflict = await ValidateAssignmentAsync(PhClock.OperationalDay, req.ShiftType, req.VehicleId, req.DriverId, null);
            if (conflict != null) return BadRequest(conflict);

            var newTrip = new Trip
            {
                // Specify UTC so Postgrest serialises as yyyy-MM-dd, matching the Index filter
                Date = DateTime.SpecifyKind(PhClock.OperationalDay, DateTimeKind.Utc),
                ShiftType = req.ShiftType,
                ShiftStartTime = startTime,
                ShiftEndTime = endTime,
                RouteId = req.RouteId,
                VehicleId = req.VehicleId,
                DriverId = req.DriverId,
                TripStatus = "Not Yet Started",
                EstimatedRevenue = 0
            };

            var insertResult = await _supabase.From<Trip>().Insert(newTrip);
            var inserted = insertResult.Models.FirstOrDefault();
            await SyncTripStatuses();

            return Ok(new { tripId = inserted?.TripId });
        }


        // ── GET options for Reassign Trip modal ───────────────────────
        [HttpGet]
        public async Task<IActionResult> GetReassignOptions(string tripId)
        {
            if (string.IsNullOrEmpty(tripId))
                return BadRequest("Trip ID is required.");

            var today = PhClock.OperationalDay.ToString("yyyy-MM-dd");

            // Fetch the trip being reassigned so we know its shift
            var tripResp = await _supabase.From<Trip>()
                .Filter("trip_id", Operator.Equals, tripId)
                .Get();
            var trip = tripResp.Models.FirstOrDefault();
            if (trip == null) return NotFound("Trip not found.");

            var tripsTask = _supabase.From<Trip>().Filter("date", Operator.Equals, today).Get();
            var vehiclesTask = _supabase.From<Vehicle>().Get();
            var driversTask = _supabase.From<UserModel>()
                                        .Filter("role_id", Operator.Equals, "2")
                                        .Filter("account_status", Operator.Equals, "Activated")
                                        .Get();
            var availTask = _supabase.From<DriverAvailability>().Get();
            var routeTask = _supabase.From<BusRoute>()
                                        .Filter("route_id", Operator.Equals, trip.RouteId.ToString())
                                        .Get();

            await Task.WhenAll(tripsTask, vehiclesTask, driversTask, availTask, routeTask);

            var todayTrips = tripsTask.Result.Models;
            var vehicles = vehiclesTask.Result.Models;
            var drivers = driversTask.Result.Models;
            var availability = availTask.Result.Models.ToDictionary(a => a.UserId, a => a.AvailabilityStatus);
            var route = routeTask.Result.Models.FirstOrDefault();

            // Vehicles already in this shift (excluding the trip being reassigned)
            var vehiclesInShift = todayTrips
                .Where(t => t.TripId != tripId && t.ShiftType == trip.ShiftType)
                .Select(t => t.VehicleId)
                .ToHashSet();

            // Drivers already in this shift (excluding the trip being reassigned)
            var driversInShift = todayTrips
                .Where(t => t.TripId != tripId && t.ShiftType == trip.ShiftType)
                .Select(t => t.DriverId)
                .ToHashSet();

            // Available vehicles: no open incident AND not already in this shift
            // Always include the trip's current vehicle so it appears as the default
            var availableVehicles = vehicles
                .Where(v => (!v.OutOfService || v.VehicleId == trip.VehicleId)
                         && (!vehiclesInShift.Contains(v.VehicleId) || v.VehicleId == trip.VehicleId))
                .OrderBy(v => v.VehicleId)
                .Select(v => new
                {
                    vehicleId = v.VehicleId,
                    plateNumber = v.PlateNumber,
                    status = v.VehicleStatus
                });

            // Available drivers: not Unavailable AND not already in this shift
            // Always include the trip's current driver so they appear as the default
            var availableDrivers = drivers
                .Where(d => (!availability.TryGetValue(d.UserId, out var s) || s != "Unavailable")
                         && (!driversInShift.Contains(d.UserId) || d.UserId == trip.DriverId))
                .OrderBy(d => d.LastName)
                .Select(d => new
                {
                    driverId = d.UserId,
                    driverName = $"{d.FirstName} {d.LastName}"
                });

            return Json(new
            {
                tripInfo = new
                {
                    tripId = trip.TripId,
                    shiftType = trip.ShiftType,
                    shiftStart = FormatShiftWindow(trip).Start,
                    shiftEnd = FormatShiftWindow(trip).End,
                    routeName = route?.RouteName ?? "—",
                    tripStatus = trip.TripStatus,
                    currentVehicleId = trip.VehicleId,
                    currentDriverId = trip.DriverId
                },
                vehicles = availableVehicles,
                drivers = availableDrivers
            });
        }

        // ── POST reassign trip ────────────────────────────────────────
        [HttpPost]
        public async Task<IActionResult> ReassignTrip([FromBody] ReassignTripRequest req)
        {
            if (req == null || string.IsNullOrEmpty(req.TripId))
                return BadRequest("Trip ID is required.");

            var tripResp = await _supabase.From<Trip>()
                .Filter("trip_id", Operator.Equals, req.TripId)
                .Get();
            var trip = tripResp.Models.FirstOrDefault();
            if (trip == null) return NotFound("Trip not found.");

            // Only update what was explicitly changed
            if (!string.IsNullOrEmpty(req.VehicleId))
                trip.VehicleId = req.VehicleId;

            if (req.DriverId.HasValue && req.DriverId.Value > 0)
                trip.DriverId = req.DriverId.Value;

            var conflict = await ValidateAssignmentAsync(trip.Date, trip.ShiftType, trip.VehicleId, trip.DriverId, trip.TripId);
            if (conflict != null) return BadRequest(conflict);

            // Use Update with filter to avoid inserting a duplicate row
            await _supabase.From<Trip>()
                .Filter("trip_id", Operator.Equals, req.TripId)
                .Set(t => t.VehicleId, trip.VehicleId)
                .Set(t => t.DriverId, trip.DriverId)
                .Update();

            await SyncTripStatuses();

            return Ok(new { tripId = trip.TripId });
        }

        // ── GET driver count (all routes, or filtered by routeId) ─────
        [HttpGet]
        public async Task<IActionResult> GetDriverCount(int? routeId)
        {
            var today = PhClock.OperationalDay.ToString("yyyy-MM-dd");

            if (routeId.HasValue)
            {
                // Count distinct drivers assigned to this route today
                var trips = await _supabase.From<Trip>()
                    .Filter("date", Operator.Equals, today)
                    .Filter("route_id", Operator.Equals, routeId.Value.ToString())
                    .Get();

                var driverIds = trips.Models.Select(t => t.DriverId).Distinct().ToList();
                return Json(new { count = driverIds.Count });
            }
            else
            {
                var drivers = await _supabase.From<UserModel>()
                    .Filter("role_id", Operator.Equals, "2")
                    .Filter("account_status", Operator.Equals, "Activated")
                    .Get();

                return Json(new { count = drivers.Models.Count });
            }
        }

        // ── POST broadcast message to all drivers ─────────────────────
        [HttpPost]
        public async Task<IActionResult> BroadcastMessage([FromBody] BroadcastMessageRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.Body))
                return BadRequest("Message body is required.");

            var senderIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            int.TryParse(senderIdClaim, out var senderId);

            await _supabase.From<Message>().Insert(new Message
            {
                SenderId = senderId,
                TargetAudience = "All",
                TargetId = null,
                Subject = req.Subject?.Trim(),
                Body = req.Body.Trim(),
                Priority = req.Priority ?? "Normal",
                CreatedAt = PhClock.NowForDb
            });

            return Ok();
        }

        // ── POST route message (all drivers on a route) ───────────────
        [HttpPost]
        public async Task<IActionResult> SendRouteMessage([FromBody] RouteMessageRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.Body) || req.RouteId == 0)
                return BadRequest("Route ID and message body are required.");

            var senderIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            int.TryParse(senderIdClaim, out var senderId);

            await _supabase.From<Message>().Insert(new Message
            {
                SenderId = senderId,
                TargetAudience = "Route",
                TargetId = req.RouteId.ToString(),
                Subject = req.Subject?.Trim(),
                Body = req.Body.Trim(),
                Priority = req.Priority ?? "Normal",
                CreatedAt = PhClock.NowForDb
            });

            return Ok();
        }

        // ── POST trip message (single driver on a trip) ───────────────
        [HttpPost]
        public async Task<IActionResult> SendTripMessage([FromBody] TripMessageRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.Body) || string.IsNullOrEmpty(req.TripId))
                return BadRequest("Trip ID and message body are required.");

            // Resolve the driver ID from the trip
            var tripResp = await _supabase.From<Trip>()
                .Filter("trip_id", Operator.Equals, req.TripId)
                .Get();
            var trip = tripResp.Models.FirstOrDefault();
            if (trip == null) return NotFound("Trip not found.");

            var senderIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            int.TryParse(senderIdClaim, out var senderId);

            await _supabase.From<Message>().Insert(new Message
            {
                SenderId = senderId,
                TargetAudience = "Driver",
                TargetId = trip.DriverId.ToString(),
                Subject = req.Subject?.Trim(),
                Body = req.Body.Trim(),
                Priority = req.Priority ?? "Normal",
                CreatedAt = PhClock.NowForDb
            });

            return Ok();
        }

        // ── Update driver availability ────────────────────────────────
        [HttpPost]
        public async Task<IActionResult> UpdateDriverAvailability(int userId, string status)
        {
            if (status != "Available" && status != "Unavailable")
                return BadRequest("Invalid status.");

            var existing = await _supabase
                .From<DriverAvailability>()
                .Filter("user_id", Operator.Equals, userId.ToString())
                .Single();

            if (existing != null)
            {
                existing.AvailabilityStatus = status;
                existing.UpdatedAt = PhClock.Now;
                await _supabase.From<DriverAvailability>().Upsert(existing);
                await SyncTripStatuses();
            }
            else
            {
                await _supabase.From<DriverAvailability>().Insert(new DriverAvailability
                {
                    UserId = userId,
                    AvailabilityStatus = status,
                    UpdatedAt = PhClock.Now
                });
            }

            return Ok();
        }

        // Format a trip's shift window on ITS OWN date; an overnight shift (end <= start)
        // rolls the end onto the next morning and gets a "(+1)" hint so it's never read as
        // ending the same morning.
        private static (string Start, string End) FormatShiftWindow(Trip t)
        {
            bool overnight = t.ShiftEndTime <= t.ShiftStartTime;
            var s = t.Date.Date.Add(t.ShiftStartTime);
            var e = t.Date.Date.Add(t.ShiftEndTime).AddDays(overnight ? 1 : 0);
            return (s.ToString("h:mm tt"), overnight ? $"{e:h:mm tt} (+1)" : e.ToString("h:mm tt"));
        }

        // Actual end as a DateTime on the trip's date (overnight rolls +1 day) — used to
        // tell whether a not-yet-started trip is already past its window (Missed).
        private static DateTime ShiftEndAt(Trip t) =>
            t.Date.Date.Add(t.ShiftEndTime).AddDays(t.ShiftEndTime <= t.ShiftStartTime ? 1 : 0);

        // Adjacent shift that immediately follows (back-to-back, same day).
        private static readonly Dictionary<string, string> NextShift = new()
        {
            ["Morning"] = "Afternoon",
            ["Afternoon"] = "Evening",
        };

        // Returns an error string if assigning (vehicle, driver) to this date/shift
        // clashes with existing trips; null if clear. Mirrors the schedule planner rules:
        //   - no driver/vehicle twice in the same shift+day
        //   - no driver in back-to-back shifts (incl. Evening -> next-day Morning)
        private async Task<string> ValidateAssignmentAsync(
            DateTime date, string shift, string vehicleId, int driverId, string excludeTripId)
        {
            var prev = date.AddDays(-1).ToString("yyyy-MM-dd");
            var next = date.AddDays(1).ToString("yyyy-MM-dd");

            // All trips on the day before/of/after — enough to judge every rule.
            var resp = await _supabase.From<Trip>()
                .Filter("date", Operator.GreaterThanOrEqual, prev)
                .Filter("date", Operator.LessThanOrEqual, next)
                .Get();
            var trips = resp.Models.Where(t => t.TripId != excludeTripId).ToList();

            string Fmt(DateTime d) => d.ToString("MMMM d, yyyy");

            // same shift + day: duplicate driver / vehicle
            foreach (var t in trips.Where(t => t.Date.Date == date.Date && t.ShiftType == shift))
            {
                if (t.DriverId == driverId)
                    return $"This driver is already booked for the {shift} shift on {Fmt(date)}.";
                if (t.VehicleId == vehicleId)
                    return $"This bus is already booked for the {shift} shift on {Fmt(date)}.";
            }

            // back-to-back for the driver (same day adjacency)
            var driverTrips = trips.Where(t => t.DriverId == driverId).ToList();
            foreach (var t in driverTrips.Where(t => t.Date.Date == date.Date))
            {
                if (NextShift.TryGetValue(shift, out var after) && t.ShiftType == after)
                    return $"This driver is booked for {shift} and {after} back to back on {Fmt(date)}. Give them a break.";
                if (NextShift.TryGetValue(t.ShiftType, out var after2) && after2 == shift)
                    return $"This driver is booked for {t.ShiftType} and {shift} back to back on {Fmt(date)}. Give them a break.";
            }

            // Evening -> next-day Morning (both directions)
            if (shift == "Evening" && driverTrips.Any(t => t.Date.Date == date.AddDays(1).Date && t.ShiftType == "Morning"))
                return $"This driver ends with Evening on {Fmt(date)} and starts Morning the next day. They need rest.";
            if (shift == "Morning" && driverTrips.Any(t => t.Date.Date == date.AddDays(-1).Date && t.ShiftType == "Evening"))
                return $"This driver works Evening the day before then Morning on {Fmt(date)}. They need rest.";

            return null;
        }

        private async Task SyncTripStatuses(string date = null)
        {
            date ??= PhClock.Today.ToString("yyyy-MM-dd");

            var tripsTask = _supabase.From<Trip>()
                                       .Filter("date", Operator.Equals, date)
                                       .Get();
            var vehiclesTask = _supabase.From<Vehicle>().Get();
            var availabilityTask = _supabase.From<DriverAvailability>().Get();

            await Task.WhenAll(tripsTask, vehiclesTask, availabilityTask);

            var trips = tripsTask.Result.Models;
            var vehicleDict = vehiclesTask.Result.Models.ToDictionary(v => v.VehicleId);
            var availabilityDict = availabilityTask.Result.Models
                                    .ToDictionary(a => a.UserId, a => a.AvailabilityStatus);

            foreach (var trip in trips)
            {
                if (trip.TripStatus == "Active" || trip.TripStatus == "Completed")
                    continue;

                vehicleDict.TryGetValue(trip.VehicleId, out var vehicle);
                availabilityDict.TryGetValue(trip.DriverId, out var driverAvail);

                string newStatus;

                // Grounded (out-of-service) bus or unavailable driver = blocking issue.
                // A flag alone no longer blocks.
                if (vehicle?.OutOfService == true || driverAvail == "Unavailable")
                    newStatus = "Assignment Issue";
                else if (vehicle?.VehicleStatus == "Pending")
                    newStatus = "Pending";
                else if (vehicle?.VehicleStatus == "Ready to Deploy" && driverAvail == "Available")
                    newStatus = "Not Yet Started";
                else
                    continue;

                if (trip.TripStatus != newStatus)
                {
                    trip.TripStatus = newStatus;
                    await _supabase.From<Trip>().Upsert(trip);
                }
            }
        }
    }


}