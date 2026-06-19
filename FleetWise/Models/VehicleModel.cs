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

    // Note: the vehicle_type column was dropped from the DB (every unit is a bus), so it's no
    // longer modeled here.

    [Column("route_id")]
    public int? RouteId { get; set; }

    [Column("capacity")]
    public int Capacity { get; set; }

    [Column("vehicle_status")]
    public string VehicleStatus { get; set; }

    // Admin-set road-safety gate, independent of the volatile vehicle_status. When true the
    // bus is grounded -> dispatch won't let it be assigned. Mobile never writes this.
    [Column("out_of_service")]
    public bool OutOfService { get; set; }

    [Column("last_maintenance_date")]
    public DateTime? LastMaintenanceDate { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}