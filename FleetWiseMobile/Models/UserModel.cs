using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWiseMobile.Models;

[Table("users")]
public class UserModel : BaseModel
{
    [PrimaryKey("user_id")]
    public int UserId { get; set; }

    [Column("first_name")]
    public string? FirstName { get; set; }

    [Column("middle_name")]
    public string? MiddleName { get; set; }

    [Column("last_name")]
    public string? LastName { get; set; }

    [Column("email_address")]
    public string? EmailAddress { get; set; }

    [Column("password_hash")]
    public string? PasswordHash { get; set; }

    [Column("role_id")]
    public int RoleId { get; set; }

    [Column("account_status")]
    public string? AccountStatus { get; set; }

    [Column("contact_number")]
    public string? ContactNumber { get; set; }

    [Column("address")]
    public string? Address { get; set; }

    [Column("emergency_contact_name")]
    public string? EmergencyContactName { get; set; }

    [Column("emergency_contact_number")]
    public string? EmergencyContactNumber { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    [Column("last_login")]
    public DateTime? LastLogin { get; set; }
}
