using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using FleetWise.Models;

namespace FleetWise.Controllers
{
    [Authorize]
    public class VehiclesController : Controller
    {
        private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

        // Fixed filter vocabularies (mirror the registry mockup's badge sets, §2.8).
        private static readonly string[] StatusFilterOptions =
            { "Ready to Deploy", "On Trip", "Pending", "Flagged" };

        private static readonly string[] ConditionFilterOptions =
            { "No Issues", "Needs Attention", "Under Repair" };

        private readonly Supabase.Client _supabase;

        public VehiclesController(Supabase.Client supabase) => _supabase = supabase;

        public async Task<IActionResult> Index(string? route, string? type, string? status, string? condition, string? search)
        {
            var (vehicles, routes, maintenance) = await LoadVehicleDataAsync();

            var vm = new VehiclesIndexViewModel
            {
                // Rows are loaded async via VehicleRows so navigation feels instant (Block 2 refinement).
                Rows = new List<VehicleListItemViewModel>(),

                TotalVehicles = vehicles.Count,
                FlaggedVehicles = vehicles.Count(v => DisplayStatus(v.VehicleStatus) == "Flagged"),
                ScheduledMaintenance = vehicles.Count(v =>
                    maintenance.TryGetValue(v.VehicleId, out var m) && m == "Under Repair"),

                RouteOptions = routes
                    .Select(r => new SelectListItem { Value = r.RouteId.ToString(), Text = r.RouteName })
                    .ToList(),
                TypeOptions = vehicles
                    .Select(v => v.VehicleType)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(t => t)
                    .ToList(),
                StatusOptions = StatusFilterOptions.ToList(),
                ConditionOptions = ConditionFilterOptions.ToList(),

                SelectedRoute = route,
                SelectedType = type,
                SelectedStatus = status,
                SelectedCondition = condition,
                SearchTerm = search,
            };

            return View(vm);
        }

        [HttpGet]
        public async Task<IActionResult> VehicleRows(string? route, string? type, string? status, string? condition, string? search)
        {
            var items = await BuildRowsAsync(route, type, status, condition, search);
            return PartialView("_VehicleRows", items);
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

        private async Task<List<VehicleListItemViewModel>> BuildRowsAsync(string? route, string? type, string? status, string? condition, string? search)
        {
            var (vehicles, routes, maintenance) = await LoadVehicleDataAsync();
            var routeNames = routes.ToDictionary(r => r.RouteId, r => r.RouteName);

            IEnumerable<Vehicle> filtered = vehicles;

            if (!string.IsNullOrWhiteSpace(route) && int.TryParse(route, out var routeId))
                filtered = filtered.Where(v => v.RouteId == routeId);

            if (!string.IsNullOrWhiteSpace(type))
                filtered = filtered.Where(v => string.Equals(v.VehicleType, type, OIC));

            if (!string.IsNullOrWhiteSpace(status))
                filtered = filtered.Where(v => string.Equals(DisplayStatus(v.VehicleStatus), status, OIC));

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
                    VehicleType = v.VehicleType ?? "",
                    RouteName = v.RouteId.HasValue && routeNames.TryGetValue(v.RouteId.Value, out var rn) ? rn : "—",
                    Status = DisplayStatus(v.VehicleStatus),
                    Maintenance = maintenance.GetValueOrDefault(v.VehicleId, "No Issues"),
                })
                .ToList();
        }

        private static string DeriveMaintenance(IEnumerable<MaintenanceLog> logs)
        {
            var open = logs
                .Where(l => l.ResolvedAt == null)
                .OrderByDescending(l => l.CreatedAt)
                .FirstOrDefault();

            return open is null ? "No Issues" : NormalizeMaintenance(open.MaintenanceStatus);
        }

        // The DB's maintenance_status vocabulary isn't fixed; map it onto the mockup's two
        // "open" badges. An unresolved log always means there's something to act on, so an
        // unknown/blank status defaults to "Needs Attention".
        private static string NormalizeMaintenance(string? maintenanceStatus)
        {
            var s = (maintenanceStatus ?? "").Trim();
            if (s.Contains("Repair", OIC)) return "Under Repair";
            if (s.Contains("No Issue", OIC) || s.Contains("Resolved", OIC)) return "No Issues";
            return "Needs Attention";
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
