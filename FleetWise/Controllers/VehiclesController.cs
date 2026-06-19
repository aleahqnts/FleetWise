using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using FleetWise.Models;
using FleetWise.Services;

namespace FleetWise.Controllers
{
    [Authorize]
    public class VehiclesController : Controller
    {
        private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

        // Fixed filter vocabularies for the Status and Issues dropdowns.
        private static readonly string[] StatusFilterOptions =
            { "Ready to Deploy", "On Trip", "Pending", "Flagged" };

        private static readonly string[] ConditionFilterOptions =
            { "No Issues", "Needs Attention", "Under Repair" };

        // The Edit modal's "Change Status" dropdown — exactly the maintenance_status_enum
        // labels. Selecting "No Issues" is the resolve action.
        private static readonly string[] MaintenanceStatusOptions =
            { "Needs Attention", "Under Repair", "No Issues" };

        private readonly Supabase.Client _supabase;

        public VehiclesController(Supabase.Client supabase) => _supabase = supabase;

        public async Task<IActionResult> Index(string? route, string? status, string? condition, string? search)
        {
            var (vehicles, routes, maintenance) = await LoadVehicleDataAsync();

            var vm = new VehiclesIndexViewModel
            {
                // Rows are loaded async via VehicleRows so navigation feels instant.
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
                Capacity = 50,                         // sensible default — not captured by the form
                VehicleStatus = "Ready to Deploy",     // new units start deployable (vehicle_status_enum label)
                CreatedAt = PhClock.Now,
            };

            await _supabase.From<Vehicle>().Insert(vehicle);

            TempData["Success"] = $"Vehicle \"{model.VehicleId}\" was added successfully.";
            return RedirectToAction(nameof(Index));
        }

        // Fetch-partial: fresh per-vehicle data for the View Details modal, no heavy page
        // payload. Combines the profile, the latest driver inspection, and the maintenance history.
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

            // Route name (route_id is nullable).
            var routeName = "—";
            if (vehicle.RouteId.HasValue)
            {
                var routeResp = await _supabase.From<BusRoute>()
                    .Filter("route_id", Postgrest.Constants.Operator.Equals, vehicle.RouteId.Value)
                    .Get();
                routeName = routeResp.Models.FirstOrDefault()?.RouteName ?? "—";
            }

            // Latest inspection for this vehicle (+ the driver who reported it).
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

            // Maintenance history, newest first.
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
            };

            if (checklist != null)
            {
                vm.HasInspection = true;
                vm.ReportedBy = DriverName(driver, checklist.DriverId);
                vm.TimeOfReport = checklist.SubmittedAt.ToString("MM/dd/yy hh:mm tt");
                vm.Issue = DeriveInspectionIssue(checklist);
                vm.Remarks = checklist.Notes;
                vm.InspectionBadge = DeriveInspectionBadge(checklist.ChecklistStatus);
            }

            vm.CurrentStatus = DeriveMaintenance(logs);
            if (logs.Count > 0)
            {
                vm.HasMaintenance = true;
                vm.IssueSummary = DeriveIssueSummary(logs[0]);
                vm.MaintenanceEntries = logs.Select(FormatMaintenanceEntry).ToList();
            }

            return PartialView("_VehicleDetails", vm);
        }

        // Fetch-partial for the Edit Vehicle modal: the editable profile + the latest
        // maintenance log, fetched fresh per vehicle (same approach as Details).
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

            // "Resolve" = the operator set the maintenance status to "No Issues".
            var resolving = string.Equals(model.MaintenanceStatus?.Trim(), "No Issues", OIC);

            // Update the maintenance log the modal was editing (a vehicle may have none).
            if (model.LogId is int logId && logId > 0 && !string.IsNullOrWhiteSpace(model.MaintenanceStatus))
            {
                var logResp = await _supabase.From<MaintenanceLog>()
                    .Filter("log_id", Postgrest.Constants.Operator.Equals, logId)
                    .Get();
                var log = logResp.Models.FirstOrDefault();
                if (log != null)
                {
                    log.MaintenanceStatus = model.MaintenanceStatus.Trim();
                    log.VerifiedBy = string.IsNullOrWhiteSpace(model.VerifiedBy) ? null : model.VerifiedBy.Trim();
                    if (resolving && log.ResolvedAt == null)
                        log.ResolvedAt = PhClock.Now;
                    await _supabase.From<MaintenanceLog>().Update(log);
                }
            }

            // Update the Vehicle Profile (vehicle_type is left as-is — every unit is a bus).
            vehicle.PlateNumber = model.PlateNumber.Trim();
            vehicle.RouteId = model.RouteId;
            vehicle.UpdatedAt = PhClock.Now;

            if (resolving)
            {
                // Stamp the last maintenance date and clear a Flagged badge so the registry
                // reflects the fix.
                vehicle.LastMaintenanceDate = PhClock.Today;
                if (string.Equals(vehicle.VehicleStatus?.Trim(), "Flagged", OIC))
                    vehicle.VehicleStatus = "Ready to Deploy";
            }

            await _supabase.From<Vehicle>().Update(vehicle);

            TempData["Success"] = $"Vehicle \"{model.VehicleId}\" was updated successfully.";
            return RedirectToAction(nameof(Index));
        }

        // ── Data loading & projection ────────────────────────────────────────

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

            // Maintenance badge = the latest *unresolved* log per vehicle, or "No Issues" when none is open.
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

            // Roadworthiness wins the registry's Status: an open incident shows as "Flagged"
            // (persistent, can't be erased by the next shift), otherwise the operational
            // status — and a stale vehicle_status of "Flagged" with no open incident is read
            // as Ready (the flag was resolved).
            string RoadStatus(Vehicle v) =>
                maintenance.GetValueOrDefault(v.VehicleId, "No Issues") != "No Issues"
                    ? "Flagged"
                    : DisplayStatus(string.Equals(v.VehicleStatus, "Flagged", OIC) ? "Ready to Deploy" : v.VehicleStatus);

            IEnumerable<Vehicle> filtered = vehicles;

            if (!string.IsNullOrWhiteSpace(route) && int.TryParse(route, out var routeId))
                filtered = filtered.Where(v => v.RouteId == routeId);

            if (!string.IsNullOrWhiteSpace(status))
                filtered = filtered.Where(v => string.Equals(RoadStatus(v), status, OIC));

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

        // Re-render the registry with the Add Vehicle modal re-opened and validation errors shown
        // (PRG can't carry ModelState, so a failed POST returns the view directly).
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

        // Builds the Edit Vehicle modal's view model: the vehicle profile, the maintenance log
        // it edits (latest unresolved, else latest overall), and self-contained dropdown data.
        // `posted` preserves the operator's input when re-rendering after a failed POST.
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
            var vehicles = (await _supabase.From<Vehicle>().Get()).Models;

            var logs = (await _supabase.From<MaintenanceLog>()
                .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, id)
                .Order("created_at", Postgrest.Constants.Ordering.Descending)
                .Get()).Models;
            // Edit the open issue if there is one, otherwise the most recent log.
            var log = logs.FirstOrDefault(l => l.ResolvedAt == null) ?? logs.FirstOrDefault();

            var vm = new EditVehicleViewModel
            {
                VehicleId = vehicle.VehicleId,
                PlateNumber = posted?.PlateNumber ?? vehicle.PlateNumber ?? "",
                RouteId = posted?.RouteId ?? vehicle.RouteId ?? 0,
                RouteOptions = BuildRouteOptions(routes),
                StatusOptions = MaintenanceStatusOptions.ToList(),
                CurrentStatus = DeriveMaintenance(logs),
            };

            if (log != null)
            {
                vm.HasMaintenance = true;
                vm.LogId = log.LogId;
                vm.DateReported = log.CreatedAt.ToString("MM/dd/yy hh:mm tt");
                vm.IssueSummary = DeriveIssueSummary(log);
                vm.MaintenanceStatus = posted?.MaintenanceStatus ?? NormalizeMaintenance(log.MaintenanceStatus);
                vm.VerifiedBy = posted?.VerifiedBy ?? log.VerifiedBy;
            }
            else
            {
                vm.LogId = posted?.LogId;
                vm.MaintenanceStatus = posted?.MaintenanceStatus;
                vm.VerifiedBy = posted?.VerifiedBy;
            }

            return vm;
        }

        // Re-render the registry with the Edit modal re-opened and its validation errors shown
        // (PRG can't carry ModelState — mirrors ReRenderIndexAsync for Add).
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

        // Supplies the Add Vehicle modal with its bound model, dropdown data, and reopen flag.
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

        // Map the stored maintenance_status onto the two "open" badges. An unresolved log
        // always means there's something to act on, so an unknown/blank status defaults to
        // "Needs Attention".
        private static string NormalizeMaintenance(string? maintenanceStatus)
        {
            var s = (maintenanceStatus ?? "").Trim();
            if (s.Contains("Repair", OIC)) return "Under Repair";
            if (s.Contains("No Issue", OIC) || s.Contains("Resolved", OIC)) return "No Issues";
            return "Needs Attention";
        }

        // Inspection "Issue" = the checklist categories whose value isn't "Pass" (the five
        // jsonb maps), humanized and de-duplicated. "None" when everything passed.
        private static string DeriveInspectionIssue(BusChecklist c)
        {
            var maps = new[]
            {
                c.ExteriorInspection, c.EngineCompartment, c.InteriorInspection,
                c.BrakeSafety, c.PassengerSystems,
            };

            var failed = maps
                .Where(m => m != null)
                .SelectMany(m => m)
                .Where(kv => !string.Equals(kv.Value?.Trim(), "Pass", OIC))
                .Select(kv => Humanize(kv.Key))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return failed.Count > 0 ? string.Join(", ", failed) : "None";
        }

        // checklist_status_enum has no "Flagged" value, so the red Flagged badge is derived:
        // Failed → Flagged; otherwise show the raw status (Passed / Pending).
        private static string DeriveInspectionBadge(string checklistStatus)
        {
            var s = (checklistStatus ?? "").Trim();
            if (s.Equals("Failed", OIC)) return "Flagged";
            return string.IsNullOrEmpty(s) ? "Pending" : s;
        }

        // "Issue Summary" for the maintenance section: the latest log's issue_details list,
        // falling back to its remarks, then a dash.
        private static string DeriveIssueSummary(MaintenanceLog latest)
        {
            if (latest.IssueDetails?.Issues is { Count: > 0 } issues)
                return string.Join(", ", issues);
            return string.IsNullOrWhiteSpace(latest.Remarks) ? "—" : latest.Remarks;
        }

        // One timeline line per log: "MM/dd/yy – ML-## – Status" (Resolved once resolved_at is set).
        private static string FormatMaintenanceEntry(MaintenanceLog log)
        {
            var date = (log.ResolvedAt ?? log.CreatedAt).ToString("MM/dd/yy");
            var status = log.ResolvedAt != null
                ? "Resolved"
                : (string.IsNullOrWhiteSpace(log.MaintenanceStatus) ? "Open" : log.MaintenanceStatus.Trim());
            return $"{date} – ML-{log.LogId:D2} – {status}";
        }

        private static string DriverName(UserModel driver, int driverId)
        {
            if (driver is null) return $"Driver #{driverId}";
            var name = string.Join(" ",
                new[] { driver.FirstName, driver.LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return string.IsNullOrWhiteSpace(name) ? $"Driver #{driverId}" : name;
        }

        // "engine_compartment" → "Engine Compartment".
        private static string Humanize(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "";
            var words = key.Replace('_', ' ').Replace('-', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", words.Select(w => char.ToUpper(w[0]) + w[1..]));
        }

        // Normalize the stored vehicle_status to the registry's display labels. Matches
        // FleetMapController's vocabulary (OnTrip/On Trip/Active are all "on a live trip").
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
    }
}
