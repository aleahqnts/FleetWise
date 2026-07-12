#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWiseMobile.Models;

[Table("vehicles")]
public class Vehicle : BaseModel
{
    [PrimaryKey("vehicle_id")]
    public string VehicleId { get; set; }

    [Column("plate_number")]
    public string PlateNumber { get; set; }

    [Column("route_id")]
    public int? RouteId { get; set; }

    [Column("capacity")]
    public int Capacity { get; set; }

    [Column("vehicle_status")]
    public string VehicleStatus { get; set; }

    // Admin grounded the bus -> driver must not start a trip on it.
    [Column("out_of_service")]
    public bool OutOfService { get; set; }

    // Phase 8: the counter phone bound to this bus (camera app claims it at bind).
    [Column("counter_device_id")]
    public string CounterDeviceId { get; set; }

    [Column("last_maintenance_date")]
    public DateTime? LastMaintenanceDate { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
