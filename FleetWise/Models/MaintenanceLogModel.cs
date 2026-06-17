#nullable disable
using System.Collections.Generic;
using Newtonsoft.Json;
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWise.Models;

[Table("maintenance_logs")]
public class MaintenanceLog : BaseModel
{
    [PrimaryKey("log_id")]
    public int LogId { get; set; }

    // Nullable: a maintenance log can be opened without an originating bus_checklist
    // (the DB column is nullable), so a non-nullable int throws on deserialize.
    [Column("checklist_id")]
    public int? ChecklistId { get; set; }

    [Column("vehicle_id")]
    public string VehicleId { get; set; }

    [Column("trip_id")]
    public string TripId { get; set; }

    // issue_details is a JSON object column in Postgres (not text), so it maps to a
    // dictionary — same pattern as Role.WebPermissions. A plain string here makes
    // Postgrest's deserializer throw on the leading '{'.
    [Column("issue_details")]
    public MaintenanceIssueDetails IssueDetails { get; set; }

    [Column("maintenance_status")]
    public string MaintenanceStatus { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("resolved_at")]
    public DateTime? ResolvedAt { get; set; }

    [Column("remarks")]
    public string Remarks { get; set; }

    // Backs the Edit Vehicle modal's "Verified by" field.
    [Column("verified_by")]
    public string VerifiedBy { get; set; }
}

// Shape of the `issue_details` jsonb column: { "issues": ["Tires", "Air Conditioning", ...] }
// Newtonsoft ignores any other/unexpected keys in the jsonb by default, so this stays
// resilient even if more fields get added to issue_details later.
public class MaintenanceIssueDetails
{
    [JsonProperty("issues")]
    public List<string> Issues { get; set; } = new();
}