using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using FleetWise.Models;
using FleetWise.Services;

namespace FleetWise.Controllers
{
    [Authorize]
    [RequirePermission("vehicles")]
    public class VehiclesController : Controller
    {
        private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

        // Fixed filter vocabularies for the Status and Issues dropdowns.
        private static readonly string[] StatusFilterOptions =
            { "Ready to Deploy", "On Trip", "Pending", "Flagged", "Out of Service" };

        private static readonly string[] ConditionFilterOptions =
            { "No Issues", "Needs Attention", "Under Repair" };

        private readonly Supabase.Client _supabase;
        private readonly AuditLog _audit;

        public VehiclesController(Supabase.Client supabase, AuditLog audit)
        {
            _supabase = supabase;
            _audit = audit;
        }

        public async Task<IActionResult> Index(string? route, string? status, string? condition, string? search)
        {
            var (vehicles, routes, maintenance) = await LoadVehicleDataAsync();

            var vm = new VehiclesIndexViewModel
            {
                // Rows load separately through VehicleRows, so the page appears immediately.
                Rows = new List<VehicleListItemViewModel>(),

                TotalVehicles = vehicles.Count,
                FlaggedVehicles = vehicles.Count(v => maintenance.GetValueOrDefault(v.VehicleId, "No Issues") != "No Issues"),
                ScheduledMaintenance = vehicles.Count(v =>
                    maintenance.TryGetValue(v.VehicleId, out var m) && m == "Under Repair"),

                RouteOptions = routes
                    .Select(r => new SelectListItem { Value = r.RouteId.ToString(), Text = r.RouteName })
                    .ToList(),
                StatusOptions = StatusFilterOptions.ToList(),
                ConditionOptions = ConditionFilterOptions.ToList(),

                SelectedRoute = route,
                SelectedStatus = status,
                SelectedCondition = condition,
                SearchTerm = search,
            };

            SetModalViewData(vm, new AddVehicleViewModel(), openModal: null);
            return View(vm);
        }

        [HttpGet]
        public async Task<IActionResult> VehicleRows(string? route, string? status, string? condition, string? search)
        {
            var items = await BuildRowsAsync(route, status, condition, search);
            return PartialView("_VehicleRows", items);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(AddVehicleViewModel model)
        {
            if (ModelState.IsValid)
            {
                var existing = await _supabase.From<Vehicle>()
                    .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, model.VehicleId.Trim())
                    .Get();

                if (existing.Models.Count > 0)
                    ModelState.AddModelError(nameof(model.VehicleId), "A vehicle with this ID already exists.");
            }

            if (!ModelState.IsValid)
                return await ReRenderIndexAsync(model);

            var vehicle = new Vehicle
            {
                VehicleId = model.VehicleId.Trim(),
                PlateNumber = model.PlateNumber.Trim(),
                RouteId = model.RouteId,
                Capacity = 50,                         // default; the form does not capture capacity
                VehicleStatus = "Ready to Deploy",     // new units start deployable (vehicle_status_enum label)
                CreatedAt = PhClock.Now,
            };

            await _supabase.From<Vehicle>().Insert(vehicle);

            await _audit.WriteAsync("vehicle_created",
                $"added bus {vehicle.VehicleId} (plate {vehicle.PlateNumber})",
                "vehicles", vehicle.VehicleId);

            TempData["Success"] = $"Vehicle \"{model.VehicleId}\" was added successfully.";
            return RedirectToAction(nameof(Index));
        }

        /// <summary>
        /// Renders the vehicle details modal: the profile, the most recent driver
        /// inspection, and the maintenance history.
        /// </summary>
        /// <remarks>Fetched per vehicle rather than sent with the registry page, which
        /// would carry this for every row.</remarks>
        [HttpGet]
        public async Task<IActionResult> Details(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return NotFound();

            var vehicleResp = await _supabase.From<Vehicle>()
                .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, id)
                .Get();
            var vehicle = vehicleResp.Models.FirstOrDefault();
            if (vehicle is null)
                return NotFound();

            // Route name, where the vehicle has a route assigned.
            var routeName = "—";
            if (vehicle.RouteId.HasValue)
            {
                var routeResp = await _supabase.From<BusRoute>()
                    .Filter("route_id", Postgrest.Constants.Operator.Equals, vehicle.RouteId.Value)
                    .Get();
                routeName = routeResp.Models.FirstOrDefault()?.RouteName ?? "—";
            }

            // The most recent inspection, with the driver who submitted it.
            var checklistResp = await _supabase.From<BusChecklist>()
                .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, id)
                .Order("submitted_at", Postgrest.Constants.Ordering.Descending)
                .Get();
            var checklist = checklistResp.Models.FirstOrDefault();

            UserModel driver = null;
            if (checklist != null)
            {
                var driverResp = await _supabase.From<UserModel>()
                    .Filter("user_id", Postgrest.Constants.Operator.Equals, checklist.DriverId)
                    .Get();
                driver = driverResp.Models.FirstOrDefault();
            }

            // Maintenance history, most recent first.
            var logsResp = await _supabase.From<MaintenanceLog>()
                .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, id)
                .Order("created_at", Postgrest.Constants.Ordering.Descending)
                .Get();
            var logs = logsResp.Models;

            var vm = new VehicleDetailsViewModel
            {
                VehicleId = vehicle.VehicleId,
                PlateNumber = vehicle.PlateNumber ?? "—",
                RouteName = routeName,
                CounterDeviceId = vehicle.CounterDeviceId,
            };

            if (checklist != null)
            {
                vm.HasInspection = true;
                vm.ReportedBy = DriverName(driver, checklist.DriverId);
                vm.TimeOfReport = checklist.SubmittedAt.ToString("MM/dd/yy hh:mm tt");
                vm.Issue = DeriveInspectionIssue(checklist);
                vm.InspectionSections = DeriveInspectionSections(checklist);
                vm.InspectionBadge = DeriveInspectionBadge(checklist.ChecklistStatus);

                // The inspection flag comes from the driver's report, and resolving
                // maintenance never edits the checklist, so a repaired bus would otherwise
                // keep its flag and failed-item list indefinitely. Once every incident is
                // closed and the bus is back in service, the flag has been dealt with and
                // is cleared. The maintenance timeline still holds the history.
                //
                // A closed incident is required, meaning logs exist, none are open and the
                // bus is not grounded. A failed checklist with no maintenance log was never
                // acted on, so that stays flagged.
                bool hadIncident = logs.Count > 0;
                bool hasOpenIncident = logs.Any(l => l.ResolvedAt == null) || vehicle.OutOfService;
                if (hadIncident && !hasOpenIncident && vm.InspectionBadge == "Flagged")
                {
                    vm.InspectionBadge = "Resolved";
                    vm.Issue = "Resolved (issues addressed)";
                    vm.InspectionSections = new();
                }
            }

            vm.CurrentStatus = DeriveMaintenance(logs);
            if (logs.Count > 0)
            {
                vm.HasMaintenance = true;
                vm.MaintenanceEntries = logs.Select(FormatMaintenanceEntry).ToList();
            }

            // Flag review: the out-of-service state, the incident to act on, and its
            // thread of comments and actions. The thread follows the open incident, or the
            // most recent one when nothing is open.
            vm.OutOfService = vehicle.OutOfService;
            var openLog = logs.FirstOrDefault(l => l.ResolvedAt == null);
            vm.OpenLogId = openLog?.LogId;
            vm.OpenIncidentCritical = openLog?.IssueDetails?.IsCritical == true;
            vm.OpenIncidentSummary = openLog?.IssueDetails?.CriticalSummary ?? "";

            // History across every incident on this vehicle, not only the open one.
            // Limiting it to a single thread hides earlier notes as soon as a second
            // incident is raised.
            var logIds = logs.Select(l => l.LogId).ToHashSet();
            if (logIds.Count > 0)
            {
                var notesResp = await _supabase.From<MaintenanceNote>()
                    .Order("created_at", Postgrest.Constants.Ordering.Descending)
                    .Get();
                // Grouped by log so each incident's lifecycle, from flagged through to
                // resolved, forms one block. Newest incident first, and newest note first
                // within each.
                vm.IncidentThreads = notesResp.Models
                    .Where(n => logIds.Contains(n.LogId))
                    .GroupBy(n => n.LogId)
                    .OrderByDescending(g => g.Max(n => n.CreatedAt))
                    .Select(g => new VehicleIncidentThreadViewModel
                    {
                        LogId = g.Key,
                        Notes = g.OrderByDescending(n => n.CreatedAt)
                            .Select(n => new VehicleNoteViewModel
                            {
                                Action = string.IsNullOrWhiteSpace(n.Action) ? "Comment" : n.Action,
                                Note = n.Note ?? "",
                                AuthorName = string.IsNullOrWhiteSpace(n.AuthorName) ? "—" : n.AuthorName,
                                // The stored digits are Philippine wall-clock time; postgrest
                                // reads them eight hours ahead, so they are normalized back.
                                When = n.CreatedAt.ToUniversalTime().ToString("MM/dd/yy hh:mm tt"),
                            }).ToList()
                    }).ToList();
            }

            return PartialView("_VehicleDetails", vm);
        }

        /// <summary>Renders the edit vehicle modal: the editable profile and the most
        /// recent maintenance log, fetched per vehicle.</summary>
        [HttpGet]
        public async Task<IActionResult> EditForm(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return NotFound();

            var vm = await BuildEditViewModelAsync(id, posted: null);
            if (vm is null)
                return NotFound();

            return PartialView("_EditVehicleForm", vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(EditVehicleViewModel model)
        {
            if (!ModelState.IsValid)
                return await ReRenderIndexForEditAsync(model);

            var vehicleResp = await _supabase.From<Vehicle>()
                .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, model.VehicleId)
                .Get();
            var vehicle = vehicleResp.Models.FirstOrDefault();
            if (vehicle is null)
            {
                TempData["Error"] = "Vehicle not found.";
                return RedirectToAction(nameof(Index));
            }

            // Profile fields only. Vehicle type is left alone because every unit is a bus.
            // The incident lifecycle belongs entirely to the actions on the details modal,
            // so editing never changes maintenance state.
            vehicle.PlateNumber = model.PlateNumber.Trim();
            vehicle.RouteId = model.RouteId;
            vehicle.UpdatedAt = PhClock.Now;

            await _supabase.From<Vehicle>().Update(vehicle);

            await _audit.WriteAsync("vehicle_updated",
                $"edited bus {model.VehicleId} (plate {vehicle.PlateNumber})",
                "vehicles", model.VehicleId);

            TempData["Success"] = $"Vehicle \"{model.VehicleId}\" was updated successfully.";
            return RedirectToAction(nameof(Index));
        }

        // Flag review actions (from the View Vehicle modal).

        /// <summary>Adds a comment to an incident's thread.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddNote(int logId, string note)
        {
            if (logId <= 0 || string.IsNullOrWhiteSpace(note))
                return BadRequest("A note is required.");

            var (uid, uname) = CurrentUser();
            await _supabase.From<MaintenanceNote>().Insert(new MaintenanceNote
            {
                LogId = logId,
                AuthorId = uid,
                AuthorName = uname,
                Action = "Comment",
                Note = note.Trim(),
                CreatedAt = PhClock.NowForDb,
            });
            return Ok();
        }

        /// <summary>
        /// Resolves an incident: closes every open log on the bus, clears the flag and the
        /// out-of-service state, and records the action.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResolveIncident(int logId, string? note)
        {
            if (logId <= 0) return BadRequest("Invalid incident.");

            var logResp = await _supabase.From<MaintenanceLog>()
                .Filter("log_id", Postgrest.Constants.Operator.Equals, logId.ToString())
                .Get();
            var clicked = logResp.Models.FirstOrDefault();
            if (clicked is null) return NotFound();

            var (uid, uname) = CurrentUser();
            var vehicleId = clicked.VehicleId;

            // Every unresolved log on the vehicle is closed, not only the one acted on. A
            // bus can hold several open incidents, and returning it to ready means none
            // remain.
            if (!string.IsNullOrEmpty(vehicleId))
            {
                var open = (await _supabase.From<MaintenanceLog>()
                        .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, vehicleId)
                        .Get()).Models
                    .Where(l => l.ResolvedAt == null)
                    .ToList();

                foreach (var l in open)
                {
                    l.ResolvedAt = PhClock.Now;
                    l.MaintenanceStatus = "No Issues";
                    if (string.IsNullOrWhiteSpace(l.VerifiedBy)) l.VerifiedBy = uname;
                    await _supabase.From<MaintenanceLog>().Update(l);
                }
            }
            else if (clicked.ResolvedAt is null)
            {
                clicked.ResolvedAt = PhClock.Now;
                clicked.MaintenanceStatus = "No Issues";
                if (string.IsNullOrWhiteSpace(clicked.VerifiedBy)) clicked.VerifiedBy = uname;
                await _supabase.From<MaintenanceLog>().Update(clicked);
            }

            // Clear the flag and return the vehicle to service.
            if (!string.IsNullOrEmpty(vehicleId))
            {
                var vResp = await _supabase.From<Vehicle>()
                    .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, vehicleId)
                    .Get();
                var vehicle = vResp.Models.FirstOrDefault();
                if (vehicle != null)
                {
                    vehicle.OutOfService = false;
                    if (string.Equals(vehicle.VehicleStatus?.Trim(), "Flagged", OIC))
                        vehicle.VehicleStatus = "Ready to Deploy";
                    vehicle.LastMaintenanceDate = PhClock.Today;
                    vehicle.UpdatedAt = PhClock.Now;
                    await _supabase.From<Vehicle>().Update(vehicle);
                }
            }

            await _supabase.From<MaintenanceNote>().Insert(new MaintenanceNote
            {
                LogId = logId,
                AuthorId = uid,
                AuthorName = uname,
                Action = "Resolved",
                Note = string.IsNullOrWhiteSpace(note) ? "Incident resolved." : note.Trim(),
                CreatedAt = PhClock.NowForDb,
            });

            // Clearing an incident returns a bus to the road, so the audit entry records
            // who approved it should the fault prove real.
            await _audit.WriteAsync("incident_resolved",
                $"cleared the incident on bus {vehicleId} and returned it to service",
                "vehicles", vehicleId);

            return Ok();
        }

        /// <summary>Grounds a bus so dispatch cannot assign it, or returns it to service.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetServiceState(string vehicleId, bool outOfService, int? logId, string? note, string? maintenanceStatus)
        {
            if (string.IsNullOrWhiteSpace(vehicleId)) return BadRequest("Vehicle required.");

            var vResp = await _supabase.From<Vehicle>()
                .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, vehicleId)
                .Get();
            var vehicle = vResp.Models.FirstOrDefault();
            if (vehicle is null) return NotFound();

            var (uid, uname) = CurrentUser();
            int? effectiveLog = logId;

            // The nature of the incident: under repair when sent to maintenance, otherwise
            // needs attention.
            var ms = string.Equals(maintenanceStatus?.Trim(), "Under Repair", OIC) ? "Under Repair" : "Needs Attention";

            if (outOfService && effectiveLog is null)
            {
                // Grounding a bus with no open incident still needs one, to hold the action
                // and any later notes.
                var insert = await _supabase.From<MaintenanceLog>().Insert(new MaintenanceLog
                {
                    VehicleId = vehicleId,
                    MaintenanceStatus = ms,
                    IssueDetails = new MaintenanceIssueDetails
                    {
                        Issues = new List<string>
                        {
                            string.IsNullOrWhiteSpace(note) ? "Taken out of service" : note.Trim()
                        }
                    },
                    CreatedAt = PhClock.NowForDb,
                });
                effectiveLog = insert.Models.FirstOrDefault()?.LogId;
            }
            else if (outOfService && effectiveLog is int openLg)
            {
                // Grounding an already-flagged bus records the chosen nature on the open
                // incident, which can promote a driver's flag to under repair.
                var logResp = await _supabase.From<MaintenanceLog>()
                    .Filter("log_id", Postgrest.Constants.Operator.Equals, openLg.ToString())
                    .Get();
                var openLog = logResp.Models.FirstOrDefault();
                if (openLog != null && !string.Equals(openLog.MaintenanceStatus?.Trim(), ms, OIC))
                {
                    openLog.MaintenanceStatus = ms;
                    await _supabase.From<MaintenanceLog>().Update(openLog);
                }
            }

            vehicle.OutOfService = outOfService;
            vehicle.UpdatedAt = PhClock.Now;
            await _supabase.From<Vehicle>().Update(vehicle);

            if (effectiveLog is int lg)
            {
                await _supabase.From<MaintenanceNote>().Insert(new MaintenanceNote
                {
                    LogId = lg,
                    AuthorId = uid,
                    AuthorName = uname,
                    Action = outOfService ? "Out of Service" : "Returned to Service",
                    Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                    CreatedAt = PhClock.NowForDb,
                });
            }

            var reason = string.IsNullOrWhiteSpace(note) ? "" : $": {note.Trim()}";
            await _audit.WriteAsync(
                outOfService ? "vehicle_grounded" : "vehicle_returned",
                outOfService
                    ? $"took bus {vehicleId} out of service ({ms}){reason}"
                    : $"returned bus {vehicleId} to service{reason}",
                "vehicles", vehicleId);

            return Ok();
        }

        /// <summary>
        /// Puts a bus into scheduled maintenance: opens an under-repair incident and
        /// grounds the bus, since a bus in the workshop is off the road.
        /// </summary>
        /// <remarks>Feeds the scheduled maintenance figure, appears in the vehicle's
        /// history, and keeps the bus out of dispatch and the schedule planner.</remarks>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ScheduleMaintenance(string vehicleId, string? note)
        {
            if (string.IsNullOrWhiteSpace(vehicleId)) return BadRequest("Vehicle required.");

            var (uid, uname) = CurrentUser();
            var insert = await _supabase.From<MaintenanceLog>().Insert(new MaintenanceLog
            {
                VehicleId = vehicleId,
                MaintenanceStatus = "Under Repair",
                IssueDetails = new MaintenanceIssueDetails
                {
                    Issues = new List<string> { string.IsNullOrWhiteSpace(note) ? "Scheduled maintenance" : note.Trim() }
                },
                CreatedAt = PhClock.NowForDb,
            });

            if (insert.Models.FirstOrDefault()?.LogId is int lg)
            {
                await _supabase.From<MaintenanceNote>().Insert(new MaintenanceNote
                {
                    LogId = lg,
                    AuthorId = uid,
                    AuthorName = uname,
                    Action = "Scheduled Maintenance",
                    Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                    CreatedAt = PhClock.NowForDb,
                });
            }

            // Take it off the road.
            var vResp = await _supabase.From<Vehicle>()
                .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, vehicleId)
                .Get();
            var vehicle = vResp.Models.FirstOrDefault();
            if (vehicle != null)
            {
                vehicle.OutOfService = true;
                vehicle.UpdatedAt = PhClock.Now;
                await _supabase.From<Vehicle>().Update(vehicle);
            }

            await _audit.WriteAsync("maintenance_scheduled",
                $"sent bus {vehicleId} to maintenance"
                    + (string.IsNullOrWhiteSpace(note) ? "" : $": {note.Trim()}"),
                "vehicles", vehicleId);

            return Ok();
        }

        /// <summary>The signed-in operator, recorded against thread entries.</summary>
        private (int? Id, string Name) CurrentUser()
        {
            var idStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            int? id = int.TryParse(idStr, out var i) ? i : null;
            var name = User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
                       ?? User.Identity?.Name ?? "Admin";
            return (id, name);
        }

        // Data loading & projection.

        private async Task<(List<Vehicle> Vehicles, List<BusRoute> Routes, Dictionary<string, string> Maintenance)> LoadVehicleDataAsync()
        {
            var vehiclesResponse = await _supabase.From<Vehicle>().Get();
            var routesResponse = await _supabase
                .From<BusRoute>()
                .Order("route_name", Postgrest.Constants.Ordering.Ascending)
                .Get();
            var logsResponse = await _supabase.From<MaintenanceLog>().Get();

            var logsByVehicle = logsResponse.Models
                .Where(l => l.VehicleId != null)
                .GroupBy(l => l.VehicleId)
                .ToDictionary(g => g.Key, g => g.AsEnumerable());

            // The maintenance badge shows the most recent unresolved log per vehicle, or
            // no issues when nothing is open.
            var maintenance = vehiclesResponse.Models.ToDictionary(
                v => v.VehicleId,
                v => DeriveMaintenance(logsByVehicle.TryGetValue(v.VehicleId, out var logs)
                    ? logs
                    : Enumerable.Empty<MaintenanceLog>()));

            return (vehiclesResponse.Models, routesResponse.Models, maintenance);
        }

        private async Task<List<VehicleListItemViewModel>> BuildRowsAsync(string? route, string? status, string? condition, string? search)
        {
            var (vehicles, routes, maintenance) = await LoadVehicleDataAsync();
            var routeNames = routes.ToDictionary(r => r.RouteId, r => r.RouteName);

            // A bus counts as on trip only while it has an active trip. The stored
            // vehicle_status column is set when a trip starts and cleared only by the
            // driver app ending it, so a trip that is removed, rolled over, or finished
            // outside the app leaves the column stuck. Deriving the state from live trips,
            // as the dispatch board and fleet map do, corrects itself instead.
            var activeVehicleIds = (await _supabase.From<Trip>()
                    .Filter("trip_status", Postgrest.Constants.Operator.Equals, "Active")
                    .Get()).Models
                .Where(t => t.VehicleId != null)
                .Select(t => t.VehicleId)
                .ToHashSet();

            // Roadworthiness takes precedence in the registry's status column. An open
            // incident shows as flagged, otherwise the bus shows as on trip when one is
            // running, or its operational status. A stored flag or trip state with no
            // incident or trip behind it reads as ready.
            string RoadStatus(Vehicle v) =>
                v.OutOfService ? "Out of Service"
                : maintenance.GetValueOrDefault(v.VehicleId, "No Issues") != "No Issues" ? "Flagged"
                : activeVehicleIds.Contains(v.VehicleId) ? "On Trip"
                : NonTripStatus(v.VehicleStatus);

            IEnumerable<Vehicle> filtered = vehicles;

            if (!string.IsNullOrWhiteSpace(route) && int.TryParse(route, out var routeId))
                filtered = filtered.Where(v => v.RouteId == routeId);

            if (!string.IsNullOrWhiteSpace(status))
            {
                if (string.Equals(status, "Flagged", OIC))
                    // An out-of-service bus is a flagged one that was grounded, so it stays
                    // in the flagged filter even though its badge reads out of service.
                    filtered = filtered.Where(v => RoadStatus(v) is "Flagged" or "Out of Service");
                else
                    filtered = filtered.Where(v => string.Equals(RoadStatus(v), status, OIC));
            }

            if (!string.IsNullOrWhiteSpace(condition))
                filtered = filtered.Where(v =>
                    string.Equals(maintenance.GetValueOrDefault(v.VehicleId, "No Issues"), condition, OIC));

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                filtered = filtered.Where(v =>
                    (v.VehicleId?.Contains(term, OIC) ?? false) ||
                    (v.PlateNumber?.Contains(term, OIC) ?? false));
            }

            return filtered
                .OrderBy(v => v.VehicleId, StringComparer.OrdinalIgnoreCase)
                .Select(v => new VehicleListItemViewModel
                {
                    VehicleId = v.VehicleId,
                    PlateNumber = v.PlateNumber ?? "",
                    RouteName = v.RouteId.HasValue && routeNames.TryGetValue(v.RouteId.Value, out var rn) ? rn : "—",
                    Status = RoadStatus(v),
                    Maintenance = maintenance.GetValueOrDefault(v.VehicleId, "No Issues"),
                })
                .ToList();
        }

        /// <summary>
        /// Re-renders the registry with the add vehicle modal open and its validation
        /// errors shown.
        /// </summary>
        /// <remarks>A redirect cannot carry model state, so a failed post returns the view
        /// directly rather than redirecting.</remarks>
        private async Task<IActionResult> ReRenderIndexAsync(AddVehicleViewModel addModel)
        {
            var (vehicles, routes, maintenance) = await LoadVehicleDataAsync();

            var vm = new VehiclesIndexViewModel
            {
                Rows = new List<VehicleListItemViewModel>(),
                TotalVehicles = vehicles.Count,
                FlaggedVehicles = vehicles.Count(v => maintenance.GetValueOrDefault(v.VehicleId, "No Issues") != "No Issues"),
                ScheduledMaintenance = vehicles.Count(v =>
                    maintenance.TryGetValue(v.VehicleId, out var um) && um == "Under Repair"),
                RouteOptions = routes
                    .Select(r => new SelectListItem { Value = r.RouteId.ToString(), Text = r.RouteName })
                    .ToList(),
                StatusOptions = StatusFilterOptions.ToList(),
                ConditionOptions = ConditionFilterOptions.ToList(),
            };

            SetModalViewData(vm, addModel, openModal: "AddVehicle");
            return View("Index", vm);
        }

        /// <summary>Builds the edit vehicle modal's model: the editable profile and the
        /// route list.</summary>
        /// <param name="posted">Values from a failed submission, preserved so the operator
        /// does not lose what they typed.</param>
        private async Task<EditVehicleViewModel?> BuildEditViewModelAsync(string id, EditVehicleViewModel? posted)
        {
            var vehicleResp = await _supabase.From<Vehicle>()
                .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, id)
                .Get();
            var vehicle = vehicleResp.Models.FirstOrDefault();
            if (vehicle is null)
                return null;

            var routes = (await _supabase.From<BusRoute>()
                .Order("route_name", Postgrest.Constants.Ordering.Ascending)
                .Get()).Models;

            return new EditVehicleViewModel
            {
                VehicleId = vehicle.VehicleId,
                PlateNumber = posted?.PlateNumber ?? vehicle.PlateNumber ?? "",
                RouteId = posted?.RouteId ?? vehicle.RouteId ?? 0,
                RouteOptions = BuildRouteOptions(routes),
            };
        }

        /// <summary>Re-renders the registry with the edit modal open and its validation
        /// errors shown, in the same way as the add path.</summary>
        private async Task<IActionResult> ReRenderIndexForEditAsync(EditVehicleViewModel editModel)
        {
            var (vehicles, routes, maintenance) = await LoadVehicleDataAsync();

            var vm = new VehiclesIndexViewModel
            {
                Rows = new List<VehicleListItemViewModel>(),
                TotalVehicles = vehicles.Count,
                FlaggedVehicles = vehicles.Count(v => maintenance.GetValueOrDefault(v.VehicleId, "No Issues") != "No Issues"),
                ScheduledMaintenance = vehicles.Count(v =>
                    maintenance.TryGetValue(v.VehicleId, out var m) && m == "Under Repair"),
                RouteOptions = BuildRouteOptions(routes),
                StatusOptions = StatusFilterOptions.ToList(),
                ConditionOptions = ConditionFilterOptions.ToList(),
            };

            SetModalViewData(vm, new AddVehicleViewModel(), openModal: "EditVehicle");
            ViewBag.EditVehicleModel = await BuildEditViewModelAsync(editModel.VehicleId, editModel);
            return View("Index", vm);
        }

        private static List<SelectListItem> BuildRouteOptions(IEnumerable<BusRoute> routes) =>
            routes
                .Select(r => new SelectListItem { Value = r.RouteId.ToString(), Text = r.RouteName })
                .ToList();

        /// <summary>Supplies the add vehicle modal with its model, dropdown data and
        /// reopen flag.</summary>
        private void SetModalViewData(VehiclesIndexViewModel vm, AddVehicleViewModel addModel, string? openModal)
        {
            ViewBag.AddVehicleModel = addModel;
            ViewBag.RouteOptions = vm.RouteOptions;
            ViewBag.OpenModal = openModal;
        }

        private static string DeriveMaintenance(IEnumerable<MaintenanceLog> logs)
        {
            var open = logs
                .Where(l => l.ResolvedAt == null)
                .OrderByDescending(l => l.CreatedAt)
                .FirstOrDefault();

            return open is null ? "No Issues" : NormalizeMaintenance(open.MaintenanceStatus);
        }

        /// <summary>
        /// Maps a stored maintenance status onto the two badges used for open incidents.
        /// </summary>
        /// <remarks>An unresolved log always means there is something to act on, so an
        /// unrecognized or empty status becomes "Needs Attention".</remarks>
        private static string NormalizeMaintenance(string? maintenanceStatus)
        {
            var s = (maintenanceStatus ?? "").Trim();
            if (s.Contains("Repair", OIC)) return "Under Repair";
            if (s.Contains("No Issue", OIC) || s.Contains("Resolved", OIC)) return "No Issues";
            return "Needs Attention";
        }

        /// <summary>
        /// The checklist sections containing at least one failed item, or "None" when
        /// everything passed.
        /// </summary>
        /// <remarks>Deliberately section-level. The maintenance issue summary lists the
        /// individual failed items, so the two complement each other rather than showing
        /// the same list twice.</remarks>
        private static string DeriveInspectionIssue(BusChecklist c)
        {
            var sections = new (string Name, Dictionary<string, string> Map)[]
            {
                ("Exterior Inspection", c.ExteriorInspection),
                ("Engine Compartment", c.EngineCompartment),
                ("Interior Inspection", c.InteriorInspection),
                ("Brake & Safety Systems", c.BrakeSafety),
                ("Passenger & Fare Systems", c.PassengerSystems),
            };

            var failed = sections
                .Where(s => s.Map != null && s.Map.Any(kv => !string.Equals(kv.Value?.Trim(), "Pass", OIC)))
                .Select(s => s.Name)
                .ToList();

            return failed.Count > 0 ? string.Join(", ", failed) : "None";
        }

        /// <summary>
        /// The badge for a checklist status. The stored enumeration has no flagged value,
        /// so a failure is shown as flagged and every other status is shown as stored.
        /// </summary>
        private static string DeriveInspectionBadge(string checklistStatus)
        {
            var s = (checklistStatus ?? "").Trim();
            // A critical failure grounds the bus; defects leave it deployable but worth
            // a look. Both read as flagged here, since both open an incident.
            if (s.Equals("Failed", OIC)) return "Flagged";
            if (s.Equals("Passed with Defects", OIC)) return "Defects";
            return string.IsNullOrEmpty(s) ? "Pending" : s;
        }

        /// <summary>
        /// Rewrites a failed checklist item into the problem it describes.
        /// </summary>
        /// <remarks>Some items are phrased negatively, where passing means the absence of
        /// a fault. Listing those unchanged under failures inverts their meaning.</remarks>
        private static readonly Dictionary<string, string> IssuePhrase = new(StringComparer.OrdinalIgnoreCase)
        {
            ["No Visible Body Damage"] = "Visible body damage",
            ["No fluid leaks under bus"] = "Fluid leak under bus",
            ["No unusual smoke or overheating"] = "Unusual smoke / overheating",
            ["No visible damage or leaks"] = "Visible damage or leaks",
        };

        private static string RephraseIssue(string issue) =>
            IssuePhrase.TryGetValue(issue?.Trim() ?? "", out var p) ? p : issue;

        /// <summary>
        /// Failed checklist items, rewritten and grouped by section, for the detail shown
        /// beneath the inspection's issue areas. Sections without a failure are omitted.
        /// </summary>
        private static List<InspectionSectionViewModel> DeriveInspectionSections(BusChecklist c)
        {
            var sections = new (string Name, Dictionary<string, string> Map)[]
            {
                ("Exterior Inspection", c.ExteriorInspection),
                ("Engine Compartment", c.EngineCompartment),
                ("Interior Inspection", c.InteriorInspection),
                ("Brake & Safety Systems", c.BrakeSafety),
                ("Passenger & Fare Systems", c.PassengerSystems),
            };

            var result = new List<InspectionSectionViewModel>();
            foreach (var s in sections)
            {
                if (s.Map is null) continue;
                var failed = s.Map
                    .Where(kv => !string.Equals(kv.Value?.Trim(), "Pass", OIC))
                    .Select(kv => RephraseIssue(kv.Key))
                    .ToList();
                if (failed.Count > 0)
                    result.Add(new InspectionSectionViewModel { Section = s.Name, Items = failed });
            }
            return result;
        }

        /// <summary>
        /// One timeline entry per log: when it happened, what the issue was in plain words,
        /// and how it ended. The internal log reference is omitted, since it means nothing
        /// to an operator.
        /// </summary>
        private static MaintenanceEntryViewModel FormatMaintenanceEntry(MaintenanceLog log)
        {
            var summary = log.IssueDetails?.Issues is { Count: > 0 } issues
                ? string.Join(", ", issues.Select(RephraseIssue))
                : (string.IsNullOrWhiteSpace(log.Remarks) ? "Maintenance" : log.Remarks.Trim());

            return new MaintenanceEntryViewModel
            {
                Date = (log.ResolvedAt ?? log.CreatedAt).ToString("MM/dd/yy"),
                Summary = summary,
                Status = log.ResolvedAt != null
                    ? "Resolved"
                    : (string.IsNullOrWhiteSpace(log.MaintenanceStatus) ? "Open" : log.MaintenanceStatus.Trim()),
                IsResolved = log.ResolvedAt != null,
            };
        }

        private static string DriverName(UserModel driver, int driverId)
        {
            if (driver is null) return $"Driver #{driverId}";
            var name = string.Join(" ",
                new[] { driver.FirstName, driver.LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return string.IsNullOrWhiteSpace(name) ? $"Driver #{driverId}" : name;
        }

        /// <summary>
        /// The status of a bus with no active trip. A stored on-trip or flagged value is
        /// stale in that case, from a trip that ended or a flag since resolved, and
        /// collapses to ready. Everything else maps through <see cref="DisplayStatus"/>.
        /// </summary>
        private static string NonTripStatus(string? vehicleStatus)
        {
            var s = (vehicleStatus ?? "").Trim();
            if (s.Equals("Flagged", OIC) || s.Equals("OnTrip", OIC) || s.Equals("On Trip", OIC) || s.Equals("Active", OIC))
                return "Ready to Deploy";
            return DisplayStatus(s);
        }

        /// <summary>
        /// Normalizes a stored vehicle status to the registry's labels, using the same
        /// vocabulary as the fleet map, where several stored spellings all mean the bus is
        /// on a live trip.
        /// </summary>
        private static string DisplayStatus(string? vehicleStatus)
        {
            var s = (vehicleStatus ?? "").Trim();
            if (string.IsNullOrEmpty(s)) return "Ready to Deploy";
            if (s.Equals("OnTrip", OIC) || s.Equals("On Trip", OIC) || s.Equals("Active", OIC)) return "On Trip";
            if (s.Equals("Flagged", OIC)) return "Flagged";
            if (s.Equals("Pending", OIC)) return "Pending";
            if (s.Equals("Ready to Deploy", OIC) || s.Equals("Ready", OIC)) return "Ready to Deploy";
            return s;
        }

        // Remote camera control for administrators: any bus at any time, with no trip
        // required.
        //
        // These endpoints exist as a proxy because the service key bypasses row-level
        // security and must never reach the browser. The underlying tables and storage are
        // the same ones the driver app uses.

        private static readonly HttpClient _camHttp = new();

        private HttpRequestMessage CamReq(HttpMethod method, string path)
        {
            var config = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var url = config["Supabase:Url"];
            var key = config["Supabase:Key"];
            var req = new HttpRequestMessage(method, $"{url}/{path}");
            req.Headers.TryAddWithoutValidation("apikey", key);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
            return req;
        }

        private async Task<System.Text.Json.JsonElement?> CamGetFirst(string path)
        {
            var res = await _camHttp.SendAsync(CamReq(HttpMethod.Get, path));
            if (!res.IsSuccessStatusCode) return null;
            var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var arr = doc.RootElement;
            return arr.GetArrayLength() > 0 ? arr[0].Clone() : null;
        }

        private async Task<bool> CamPatch(string deviceId, object body)
        {
            var req = CamReq(HttpMethod.Patch,
                $"rest/v1/device_config?device_id=eq.{Uri.EscapeDataString(deviceId)}");
            req.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body),
                System.Text.Encoding.UTF8, "application/json");
            var res = await _camHttp.SendAsync(req);
            return res.IsSuccessStatusCode;
        }

        /// <summary>Panel state: the device, its desired configuration, and what it
        /// reports.</summary>
        [HttpGet]
        public async Task<IActionResult> CameraState(string vehicleId)
        {
            var vResp = await _supabase.From<Vehicle>()
                .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, vehicleId)
                .Get();
            var dev = vResp.Models.FirstOrDefault()?.CounterDeviceId;
            if (string.IsNullOrEmpty(dev))
                return Json(new { deviceId = (string?)null });

            var esc = Uri.EscapeDataString(dev);
            var cfg = await CamGetFirst($"rest/v1/device_config?device_id=eq.{esc}");
            var st = await CamGetFirst($"rest/v1/device_status?device_id=eq.{esc}");
            return Json(new { deviceId = dev, config = cfg, status = st });
        }

        /// <summary>Asks the camera to wake and take a fresh photo of the doorway.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CameraWake(string deviceId)
        {
            var ok = await CamPatch(deviceId, new { wake_requested_at = DateTime.UtcNow });
            return ok ? Ok() : StatusCode(502);
        }

        /// <summary>
        /// Serves the camera's snapshot to the browser.
        /// </summary>
        /// <remarks>
        /// The storage bucket is private and the key stays on the server, so the image is
        /// proxied rather than linked. Caching is disabled because the object is
        /// overwritten in place on every wake, and a cached copy would show an earlier
        /// photo of the doorway.
        /// </remarks>
        [HttpGet]
        public async Task<IActionResult> CameraSnapshot(string deviceId)
        {
            var req = CamReq(HttpMethod.Get,
                $"storage/v1/object/authenticated/camera-snapshots/{Uri.EscapeDataString(deviceId)}.jpg");
            var res = await _camHttp.SendAsync(req);
            if (!res.IsSuccessStatusCode) return NotFound();
            var bytes = await res.Content.ReadAsByteArrayAsync();
            Response.Headers.CacheControl = "no-store";
            return File(bytes, "image/jpeg");
        }

        /// <summary>
        /// Saves a calibration to the camera's configuration.
        /// </summary>
        /// <remarks>
        /// The version is re-read immediately before the write, so a concurrent editor,
        /// whether the driver app or the camera's own calibration screen, cannot take the
        /// same number with different content. The camera skips any version that is not
        /// strictly greater, so a collision would be silently ignored.
        ///
        /// The coordinates arrive as invariant-culture strings and are parsed as such.
        /// Form binding is culture-sensitive and would misread a decimal point under some
        /// locales.
        /// </remarks>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CameraSave(
            string deviceId, string ax, string ay, string bx, string by,
            int inwardSign, bool useBack)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (!double.TryParse(ax, System.Globalization.NumberStyles.Float, inv, out var nAx) ||
                !double.TryParse(ay, System.Globalization.NumberStyles.Float, inv, out var nAy) ||
                !double.TryParse(bx, System.Globalization.NumberStyles.Float, inv, out var nBx) ||
                !double.TryParse(by, System.Globalization.NumberStyles.Float, inv, out var nBy))
                return BadRequest();

            var cfg = await CamGetFirst(
                $"rest/v1/device_config?device_id=eq.{Uri.EscapeDataString(deviceId)}&select=version");
            var curV = cfg?.TryGetProperty("version", out var v) == true ? v.GetInt32() : 0;
            var newV = curV + 1;

            var ok = await CamPatch(deviceId, new
            {
                line_ax = nAx,
                line_ay = nAy,
                line_bx = nBx,
                line_by = nBy,
                inward_sign = inwardSign,
                use_back_camera = useBack,
                version = newV,
                updated_by = "admin",
                updated_at = DateTime.UtcNow
            });

            // The counting line determines the passenger count, which determines the
            // revenue figure, so a change to it is worth attributing. The database trigger
            // records what changed; this records who changed it.
            //
            // Only a save is recorded. Requesting a fresh photo writes to a separate
            // column that the trigger ignores for the same reason.
            if (ok)
                await _audit.WriteAsync("camera_calibrated",
                    $"saved a new counting line for camera {deviceId} (v{newV})",
                    "device_config", deviceId);

            return ok ? Json(new { version = newV }) : StatusCode(502);
        }
    }
}
