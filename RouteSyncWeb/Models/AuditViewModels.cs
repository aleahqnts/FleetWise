namespace FleetWise.Models
{
    /// <summary>One entry of the audit trail, as shown on the audit log page.</summary>
    public class AuditEntryViewModel
    {
        public long Id { get; set; }
        public DateTimeOffset OccurredAt { get; set; }   // stored UTC, displayed as PH time
        public string ActorType { get; set; } = "";      // user | device | admin | system | anon
        public string? ActorId { get; set; }
        public string? ActorRole { get; set; }
        public string Action { get; set; } = "";
        public string? TargetTable { get; set; }
        public string? TargetId { get; set; }
        public string Source { get; set; } = "";         // db | edge | web
        public string Outcome { get; set; } = "";        // ok | denied | error
        public string? Summary { get; set; }
        public string? Changes { get; set; }             // pretty-printed jsonb, or null
        public string? Ip { get; set; }
    }

    public class AuditIndexViewModel
    {
        public List<AuditEntryViewModel> Entries { get; set; } = new();

        // Null means the read itself failed, which is different from "nothing matched".
        // The page says so rather than pretending the trail is empty.
        public bool LoadFailed { get; set; }

        public int Page { get; set; } = 1;
        public int PageSize { get; set; }
        public int Total { get; set; }
        public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));

        // Filter state, echoed back into the form and the paging links.
        public string? Type { get; set; }
        public string? Source { get; set; }
        public string? Outcome { get; set; }
        public string? Query { get; set; }
        public string? From { get; set; }
        public string? To { get; set; }

        public bool HasFilters =>
            !string.IsNullOrWhiteSpace(Type) || !string.IsNullOrWhiteSpace(Source)
            || !string.IsNullOrWhiteSpace(Outcome) || !string.IsNullOrWhiteSpace(Query)
            || !string.IsNullOrWhiteSpace(From) || !string.IsNullOrWhiteSpace(To);
    }
}
