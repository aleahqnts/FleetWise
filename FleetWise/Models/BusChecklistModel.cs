#nullable disable
using System.Collections.Generic;
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

    // These five are jsonb columns in Postgres — flat { "item": "Pass"/"Fail" } maps, not
    // text — so they deserialize to a dictionary (a plain string makes Postgrest's deserializer
    // throw on the leading '{'). The inspection "Issue" is derived from the entries whose
    // value isn't "Pass".
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