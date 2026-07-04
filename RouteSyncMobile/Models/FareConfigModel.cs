#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWiseMobile.Models;

[Table("fare_config")]
public class FareConfig : BaseModel
{
    [PrimaryKey("id")]
    public int Id { get; set; }

    [Column("standard_fare")]
    public decimal StandardFare { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
