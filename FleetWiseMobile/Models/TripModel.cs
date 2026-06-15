#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWiseMobile.Models;

[Table("trips")]
public class Trip : BaseModel
{
    [PrimaryKey("trip_id")]
    public string TripId { get; set; }

    [Column("date")]
    public DateTime Date { get; set; }

    [Column("shift_type")]
    public string ShiftType { get; set; }

    [Column("shift_start_time")]
    public TimeSpan ShiftStartTime { get; set; }

    [Column("shift_end_time")]
    public TimeSpan ShiftEndTime { get; set; }

    [Column("route_id")]
    public int RouteId { get; set; }

    [Column("vehicle_id")]
    public string VehicleId { get; set; }

    [Column("driver_id")]
    public int DriverId { get; set; }

    [Column("trip_status")]
    public string TripStatus { get; set; }

    [Column("estimated_revenue")]
    public decimal EstimatedRevenue { get; set; }

    // --- Added for the driver app (manual passenger counting) ---
    [Column("total_boarded")]
    public int TotalBoarded { get; set; }

    [Column("actual_start_time")]
    public DateTime? ActualStartTime { get; set; }

    [Column("actual_end_time")]
    public DateTime? ActualEndTime { get; set; }
}
