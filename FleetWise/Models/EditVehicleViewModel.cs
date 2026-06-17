using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace FleetWise.Models
{
    // Backs the Edit Vehicle modal (Block 17): the editable Vehicle Profile plus the latest
    // Maintenance Log's Change Status / Verified by. Carries its own dropdown + display data so
    // the partial is self-sufficient whether it's fetched (GET EditForm) or re-rendered on a
    // failed POST.
    public class EditVehicleViewModel
    {
        // Read-only — the PK can't change (mockup greys it out).
        [Display(Name = "Vehicle ID")]
        public string VehicleId { get; set; } = string.Empty;

        [Required, StringLength(20)]
        [Display(Name = "Plate Number")]
        public string PlateNumber { get; set; } = string.Empty;

        [Required, StringLength(50)]
        [Display(Name = "Vehicle Type")]
        public string VehicleType { get; set; } = string.Empty;

        [Required, Range(1, int.MaxValue, ErrorMessage = "Please select a route.")]
        [Display(Name = "Route")]
        public int RouteId { get; set; }

        // ── Maintenance Log (latest; optional — a vehicle may have none) ──
        public int? LogId { get; set; }

        [Display(Name = "Change Status")]
        public string? MaintenanceStatus { get; set; }

        [StringLength(100)]
        [Display(Name = "Verified by")]
        public string? VerifiedBy { get; set; }

        // ── Display-only (rebuilt server-side; not authoritative on POST) ──
        public bool HasMaintenance { get; set; }
        public string DateReported { get; set; } = "—";
        public string IssueSummary { get; set; } = "—";
        /// <summary>Current maintenance badge: No Issues / Needs Attention / Under Repair.</summary>
        public string CurrentStatus { get; set; } = "No Issues";

        // ── Dropdown data ──
        public List<SelectListItem> RouteOptions { get; set; } = new();
        public List<SelectListItem> TypeOptions { get; set; } = new();
        public List<string> StatusOptions { get; set; } = new();
    }
}
