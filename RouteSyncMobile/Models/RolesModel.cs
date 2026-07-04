#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWiseMobile.Models;

[Table("roles")]
public class Role : BaseModel
{
    [PrimaryKey("role_id")]
    public int RoleId { get; set; }

    [Column("role_name")]
    public string RoleName { get; set; }

    [Column("access_level")]
    public string AccessLevel { get; set; }

    [Column("web_permissions")]
    public Dictionary<string, bool> WebPermissions { get; set; }

    [Column("mobile_permissions")]
    public Dictionary<string, bool> MobilePermissions { get; set; }
}
