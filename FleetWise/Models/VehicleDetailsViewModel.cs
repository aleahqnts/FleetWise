namespace FleetWise.Models
{
    // Read-only projection for the View Vehicle Details modal: Vehicle Profile + latest driver
    // Inspection Log + Maintenance Log history, fetched fresh per vehicle.
    public class VehicleDetailsViewModel
    {
        public string VehicleId { get; set; } = string.Empty;

        // ── Vehicle Profile ──
        public string PlateNumber { get; set; } = string.Empty;
        public string RouteName { get; set; } = string.Empty;

        // ── Inspection Log (latest bus_checklist) ──
        public bool HasInspection { get; set; }
        public string ReportedBy { get; set; } = string.Empty;
        public string TimeOfReport { get; set; } = string.Empty;
        /// <summary>Derived from the checklist categories whose value isn't "Pass".</summary>
        public string Issue { get; set; } = string.Empty;
        public string Remarks { get; set; } = string.Empty;
        /// <summary>Derived badge: Failed → Flagged, else Passed / Pending.</summary>
        public string InspectionBadge { get; set; } = string.Empty;

        // ── Maintenance Log ──
        public bool HasMaintenance { get; set; }
        /// <summary>Mockup badge: No Issues / Needs Attention / Under Repair.</summary>
        public string CurrentStatus { get; set; } = "No Issues";
        public string IssueSummary { get; set; } = string.Empty;
        /// <summary>Newest-first, each formatted "MM/dd/yy – ML-## – Status".</summary>
        public List<string> MaintenanceEntries { get; set; } = new();

        // ── Flag review / actions ──
        /// <summary>Admin road-safety gate: bus is grounded, dispatch can't assign it.</summary>
        public bool OutOfService { get; set; }
        /// <summary>The open (unresolved) incident to act on — resolve / note. Null = none open.</summary>
        public int? OpenLogId { get; set; }
        /// <summary>Audit thread for the open (or latest) incident, newest first.</summary>
        public List<VehicleNoteViewModel> Notes { get; set; } = new();
    }

    // One audit-thread line for the View Vehicle modal.
    public class VehicleNoteViewModel
    {
        public string Action { get; set; } = string.Empty;
        public string Note { get; set; } = string.Empty;
        public string AuthorName { get; set; } = string.Empty;
        public string When { get; set; } = string.Empty;
    }
}
