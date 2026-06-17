#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWise.Models;

// Aleah's single-row fare configuration table (id defaults to 1). The standard fare the
// Fleet Map's revenue estimate reads — replaces the appsettings constant as the source of truth.
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
