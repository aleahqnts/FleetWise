#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWise.Models;

[Table("bus_checklist")]
public class BusChecklist : BaseModel
{
    [PrimaryKey("checklist_id")]
    public int ChecklistId { get; set; }

    [Column("trip_id")]
    public string TripId { get; set; }

    [Column("vehicle_id")]
    public string VehicleId { get; set; }

    [Column("driver_id")]
    public int DriverId { get; set; }

    [Column("submitted_at")]
    public DateTime SubmittedAt { get; set; }

    [Column("exterior_inspection")]
    public string ExteriorInspection { get; set; }

    [Column("engine_compartment")]
    public string EngineCompartment { get; set; }

    [Column("interior_inspection")]
    public string InteriorInspection { get; set; }

    [Column("brake_safety")]
    public string BrakeSafety { get; set; }

    [Column("passenger_systems")]
    public string PassengerSystems { get; set; }

    [Column("checklist_status")]
    public string ChecklistStatus { get; set; }

    [Column("notes")]
    public string Notes { get; set; }
}