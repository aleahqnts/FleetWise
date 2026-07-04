#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWiseMobile.Models;

[Table("routes")]
public class BusRoute : BaseModel
{
    [PrimaryKey("route_id")]
    public int RouteId { get; set; }

    [Column("route_name")]
    public string RouteName { get; set; }

    [Column("origin")]
    public string Origin { get; set; }

    [Column("destination")]
    public string Destination { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
