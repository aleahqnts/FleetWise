#nullable disable

using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWise.Models;

[Table("telemetry_data")]
public class TelemetryData : BaseModel
{
    [PrimaryKey("telemetry_id")]
    public long TelemetryId { get; set; }

    [Column("trip_id")]
    public string TripId { get; set; }

    [Column("latitude")]
    public decimal Latitude { get; set; }

    [Column("longitude")]
    public decimal Longitude { get; set; }

    [Column("current_passengers")]
    public int CurrentPassengers { get; set; }

    [Column("speed")]
    public decimal? Speed { get; set; }

    [Column("heading")]
    public float? Heading { get; set; }

    [Column("timestamp")]
    public DateTime Timestamp { get; set; }
}