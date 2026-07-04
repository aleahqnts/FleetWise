using SQLite;

namespace FleetWiseMobile.Models;

// Durable "trip ended" record. EndTrip enqueues this so the final boarded count
// + revenue reach Supabase even if the driver was offline when ending — fixes
// trips.total_boarded drifting below the telemetry last-log (audit accuracy).
public class PendingTripFinalize
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    public string TripId { get; set; } = "";
    public int TotalBoarded { get; set; }
    public decimal Revenue { get; set; }
    public DateTime EndTime { get; set; }
}
