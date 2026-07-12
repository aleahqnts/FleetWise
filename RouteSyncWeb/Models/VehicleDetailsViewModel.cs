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
        /// <summary>Phase 8: counter phone bound to this bus (null/empty = none bound).</summary>
        public string? CounterDeviceId { get; set; }

        // ── Inspection Log (latest bus_checklist) ──
        public bool HasInspection { get; set; }
        public string ReportedBy { get; set; } = string.Empty;
        public string TimeOfReport { get; set; } = string.Empty;
        /// <summary>The flagged areas — checklist sections whose value isn't "Pass".</summary>
        public string Issue { get; set; } = string.Empty;
        /// <summary>Failed checklist items (rephrased) grouped by section — the collapsible flag detail.</summary>
        public List<InspectionSectionViewModel> InspectionSections { get; set; } = new();
        /// <summary>Derived badge: Failed → Flagged, else Passed / Pending.</summary>
        public string InspectionBadge { get; set; } = string.Empty;

        // ── Maintenance Log ──
        public bool HasMaintenance { get; set; }
        /// <summary>Mockup badge: No Issues / Needs Attention / Under Repair.</summary>
        public string CurrentStatus { get; set; } = "No Issues";
        /// <summary>Maintenance timeline, newest first: date + what the issue was + outcome.</summary>
        public List<MaintenanceEntryViewModel> MaintenanceEntries { get; set; } = new();

        // ── Flag review / actions ──
        /// <summary>Admin road-safety gate: bus is grounded, dispatch can't assign it.</summary>
        public bool OutOfService { get; set; }
        /// <summary>The open (unresolved) incident to act on — resolve / note. Null = none open.</summary>
        public int? OpenLogId { get; set; }
        /// <summary>Audit history grouped by incident (one maintenance lifecycle = one log_id),
        /// newest incident first, newest note first within each.</summary>
        public List<VehicleIncidentThreadViewModel> IncidentThreads { get; set; } = new();
    }

    // One line in the Maintenance Log timeline: when, what the issue was, and the outcome.
    public class MaintenanceEntryViewModel
    {
        public string Date { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;   // the issue, in plain words
        public string Status { get; set; } = string.Empty;     // Resolved / Under Repair / …
        public bool IsResolved { get; set; }
    }

    // One incident's audit thread (every note sharing a log_id) — a single flagged→resolved
    // lifecycle, rendered as one visually separated block in the History.
    public class VehicleIncidentThreadViewModel
    {
        public int LogId { get; set; }
        public List<VehicleNoteViewModel> Notes { get; set; } = new();
    }

    // One inspection section + its failed items, for the collapsible flag detail.
    public class InspectionSectionViewModel
    {
        public string Section { get; set; } = string.Empty;
        public List<string> Items { get; set; } = new();
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
