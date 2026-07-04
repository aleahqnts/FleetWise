#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWiseMobile.Models;

[Table("driver_availability")]
public class DriverAvailability : BaseModel
{
    [PrimaryKey("user_id")]
    public int UserId { get; set; }

    [Column("availability_status")]
    public string AvailabilityStatus { get; set; }

    [Column("reason")]
    public string Reason { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
