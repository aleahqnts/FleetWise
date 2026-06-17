using Microsoft.AspNetCore.Mvc.Rendering;

namespace FleetWise.Models
{
    public class VehiclesIndexViewModel
    {
        /// <summary>Empty on the initial page render — loaded async via the VehicleRows partial.</summary>
        public List<VehicleListItemViewModel> Rows { get; set; } = new();

        // ── Summary cards (computed over ALL vehicles, unaffected by the table filters) ──
        public int TotalVehicles { get; set; }
        public int FlaggedVehicles { get; set; }
        public int ScheduledMaintenance { get; set; }

        // ── Dropdown option lists ──
        public List<SelectListItem> RouteOptions { get; set; } = new();
        public List<string> StatusOptions { get; set; } = new();
        public List<string> ConditionOptions { get; set; } = new();

        // ── Selected filter state (echoed back so dropdowns/search keep their value) ──
        public string? SelectedRoute { get; set; }
        public string? SelectedStatus { get; set; }
        public string? SelectedCondition { get; set; }
        public string? SearchTerm { get; set; }
    }
}
