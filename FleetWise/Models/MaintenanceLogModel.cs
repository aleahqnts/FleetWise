#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWise.Models;

[Table("maintenance_logs")]
public class MaintenanceLog : BaseModel
{
    [PrimaryKey("log_id")]
    public int LogId { get; set; }

    [Column("checklist_id")]
    public int ChecklistId { get; set; }

    [Column("vehicle_id")]
    public string VehicleId { get; set; }

    [Column("trip_id")]
    public string TripId { get; set; }

    [Column("issue_details")]
    public string IssueDetails { get; set; }

    [Column("maintenance_status")]
    public string MaintenanceStatus { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("resolved_at")]
    public DateTime? ResolvedAt { get; set; }

    [Column("remarks")]
    public string Remarks { get; set; }
}