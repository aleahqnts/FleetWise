using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWiseMobile.Models;

// Phase 7 transition-only model: the base `users` table, used EXCLUSIVELY by the
// anon fallback paths that still verify/write the password hash client-side
// (offline login fallback, password change fallback). Dies with the 7b cutover —
// once RLS flips on `users`, only the edge functions can touch the hash.
[Table("users")]
public class UserAuthModel : BaseModel
{
    [PrimaryKey("user_id")]
    public int UserId { get; set; }

    [Column("email_address")]
    public string? EmailAddress { get; set; }

    [Column("password_hash")]
    public string? PasswordHash { get; set; }

    [Column("role_id")]
    public int RoleId { get; set; }

    [Column("account_status")]
    public string? AccountStatus { get; set; }
}
