#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWise.Models;

[Table("vehicles")]
public class Vehicle : BaseModel
{
    // shouldInsert: true — unlike the serial PKs (user_id/role_id), vehicle_id is a
    // user-entered varchar with no DB default, so it must be included on Insert.
    // (PrimaryKey defaults to shouldInsert:false, which silently sent null → 23502.)
    [PrimaryKey("vehicle_id", true)]
    public string VehicleId { get; set; }

    [Column("plate_number")]
    public string PlateNumber { get; set; }

    [Column("vehicle_type")]
    public string VehicleType { get; set; }

    [Column("route_id")]
    public int? RouteId { get; set; }

    [Column("capacity")]
    public int Capacity { get; set; }

    [Column("vehicle_status")]
    public string VehicleStatus { get; set; }

    [Column("last_maintenance_date")]
    public DateTime? LastMaintenanceDate { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}