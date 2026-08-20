using Microsoft.AspNetCore.Mvc.Rendering;

namespace FleetWise.Models
{
    public class VehiclesIndexViewModel
    {
        /// <summary>Empty on the first render. The rows load separately through their own
        /// partial view.</summary>
        public List<VehicleListItemViewModel> Rows { get; set; } = new();

        // Summary cards (computed over ALL vehicles, unaffected by the table filters).
        public int TotalVehicles { get; set; }
        public int FlaggedVehicles { get; set; }
        public int ScheduledMaintenance { get; set; }

        // Dropdown option lists.
        public List<SelectListItem> RouteOptions { get; set; } = new();

        /// <summary>Buses that have left the fleet, counted apart from it.</summary>
        public int RetiredVehicles { get; set; }

        /// <summary>The identifier the next added bus will receive.</summary>
        public string NextVehicleId { get; set; } = "";
        public List<string> StatusOptions { get; set; } = new();
        public List<string> ConditionOptions { get; set; } = new();

        // Selected filter state (echoed back so dropdowns/search keep their value).
        public string? SelectedRoute { get; set; }
        public string? SelectedStatus { get; set; }
        public string? SelectedCondition { get; set; }
        public string? SearchTerm { get; set; }
    }
}
