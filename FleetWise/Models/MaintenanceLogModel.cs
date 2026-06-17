#nullable disable
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
    public Dictionary<string, object> IssueDetails { get; set; }

    [Column("maintenance_status")]
    public string MaintenanceStatus { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("resolved_at")]
    public DateTime? ResolvedAt { get; set; }

    [Column("remarks")]
    public string Remarks { get; set; }

    // Added for the Edit Vehicle modal's "Verified by" field (Block 17). Requires the
    // additive column run in the Supabase SQL Editor:
    //   ALTER TABLE maintenance_logs ADD COLUMN IF NOT EXISTS verified_by text;
    [Column("verified_by")]
    public string VerifiedBy { get; set; }
}