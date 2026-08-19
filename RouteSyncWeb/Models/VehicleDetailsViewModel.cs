namespace FleetWise.Models
{
    /// <summary>
    /// Read-only projection for the vehicle details modal: the profile, the most recent
    /// driver inspection, and the maintenance history. Fetched per vehicle.
    /// </summary>
    public class VehicleDetailsViewModel
    {
        public string VehicleId { get; set; } = string.Empty;

        // Vehicle Profile.
        public string PlateNumber { get; set; } = string.Empty;
        public string RouteName { get; set; } = string.Empty;
        /// <summary>The counter phone bound to this bus, or empty when none is bound.</summary>
        public string? CounterDeviceId { get; set; }

        // Inspection Log (latest bus_checklist).
        public bool HasInspection { get; set; }
        public string ReportedBy { get; set; } = string.Empty;
        public string TimeOfReport { get; set; } = string.Empty;
        /// <summary>The flagged areas: checklist sections that did not pass.</summary>
        public string Issue { get; set; } = string.Empty;
        /// <summary>Failed checklist items, rewritten and grouped by section.</summary>
        public List<InspectionSectionViewModel> InspectionSections { get; set; } = new();
        /// <summary>The badge shown for the inspection: a failure reads as flagged.</summary>
        public string InspectionBadge { get; set; } = string.Empty;

        // Maintenance Log.
        public bool HasMaintenance { get; set; }
        /// <summary>The maintenance badge: no issues, needs attention, or under repair.</summary>
        public string CurrentStatus { get; set; } = "No Issues";
        /// <summary>Maintenance timeline, newest first: when, what the issue was, and the outcome.</summary>
        public List<MaintenanceEntryViewModel> MaintenanceEntries { get; set; } = new();

        // Flag review / actions.
        /// <summary>Whether the bus is grounded, which prevents dispatch assigning it.</summary>
        public bool OutOfService { get; set; }
        /// <summary>The unresolved incident to act on, or null when none is open.</summary>
        public int? OpenLogId { get; set; }
        /// <summary>
        /// History grouped by incident, so each maintenance lifecycle forms one block.
        /// Newest incident first, and newest note first within each.
        /// </summary>
        public List<VehicleIncidentThreadViewModel> IncidentThreads { get; set; } = new();
    }

    /// <summary>One entry in the maintenance timeline.</summary>
    public class MaintenanceEntryViewModel
    {
        public string Date { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;   // the issue, in plain words
        public string Status { get; set; } = string.Empty;     // Resolved / Under Repair / …
        public bool IsResolved { get; set; }
    }

    /// <summary>
    /// One incident's thread: every note against the same log, covering a single lifecycle
    /// from flagged through to resolved, rendered as one block.
    /// </summary>
    public class VehicleIncidentThreadViewModel
    {
        public int LogId { get; set; }
        public List<VehicleNoteViewModel> Notes { get; set; } = new();
    }

    /// <summary>One inspection section and the items in it that failed.</summary>
    public class InspectionSectionViewModel
    {
        public string Section { get; set; } = string.Empty;
        public List<string> Items { get; set; } = new();
    }

    /// <summary>One entry in an incident's thread.</summary>
    public class VehicleNoteViewModel
    {
        public string Action { get; set; } = string.Empty;
        public string Note { get; set; } = string.Empty;
        public string AuthorName { get; set; } = string.Empty;
        public string When { get; set; } = string.Empty;
    }
}
