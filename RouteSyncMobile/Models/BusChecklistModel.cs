#nullable disable
using System.Collections.Generic;
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWiseMobile.Models;

[Table("bus_checklist")]
public class BusChecklist : BaseModel
{
    // shouldInsert: false -> let the DB generate the id on insert.
    [PrimaryKey("checklist_id", false)]
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
    public Dictionary<string, string> ExteriorInspection { get; set; }

    [Column("engine_compartment")]
    public Dictionary<string, string> EngineCompartment { get; set; }

    [Column("interior_inspection")]
    public Dictionary<string, string> InteriorInspection { get; set; }

    [Column("brake_safety")]
    public Dictionary<string, string> BrakeSafety { get; set; }

    [Column("passenger_systems")]
    public Dictionary<string, string> PassengerSystems { get; set; }

    [Column("checklist_status")]
    public string ChecklistStatus { get; set; }

    [Column("notes")]
    public string Notes { get; set; }
}
