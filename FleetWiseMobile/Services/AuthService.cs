using FleetWiseMobile.Models;
using Microsoft.AspNetCore.Identity;

namespace FleetWiseMobile.Services;

// Mirrors the web app's AuthService: look up the user by email in the Supabase
// `users` table, verify the password hash with the same PasswordHasher the
// dashboard uses, and only allow Activated drivers (role_id = 2).
public class AuthService
{
    private const int DriverRoleId = 2;

    // Shared temp password new accounts start on (mirrors web PasswordPolicy). A login that
    // used exactly this value means the driver hasn't set their own password yet.
    public const string TemporaryPassword = "@Temp123";

    private readonly Supabase.Client _supabase;

    public AuthService(Supabase.Client supabase) => _supabase = supabase;

    public async Task<UserModel?> ValidateAsync(string email, string password)
    {
        var resp = await _supabase
            .From<UserModel>()
            .Filter("email_address", Postgrest.Constants.Operator.Equals, email)
            .Get();

        var user = resp.Models.FirstOrDefault();
        if (user is null || user.PasswordHash is null || user.AccountStatus != "Activated")
            return null;

        // Driver app is for drivers only.
        if (user.RoleId != DriverRoleId)
            return null;

        var hasher = new PasswordHasher<UserModel>();
        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        return result == PasswordVerificationResult.Failed ? null : user;
    }
}
