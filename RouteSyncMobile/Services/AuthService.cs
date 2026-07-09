using FleetWiseMobile.Models;
using Microsoft.AspNetCore.Identity;

namespace FleetWiseMobile.Services;

// Phase 7 login: the auth-login edge function verifies the password SERVER-side and
// mints a 30-day app_driver JWT. If the function itself is unreachable (7a migration
// window / fn not deployed), we fall back to the legacy client-side verify against the
// base users table — that path dies with the 7b cutover. A definitive "wrong password"
// from the server NEVER falls back.
public class AuthService
{
    private const int DriverRoleId = 2;

    // Shared temp password new accounts start on (mirrors web PasswordPolicy). A login that
    // used exactly this value means the driver hasn't set their own password yet.
    public const string TemporaryPassword = "@Temp123";

    private readonly Supabase.Client _supabase;
    private readonly AuthApi _authApi;

    public AuthService(Supabase.Client supabase, AuthApi authApi)
    {
        _supabase = supabase;
        _authApi = authApi;
    }

    public async Task<UserModel?> ValidateAsync(string email, string password)
    {
        // Primary: edge function (server-side verify + JWT). Setting SupabaseConfig.Jwt
        // is all it takes — the postgrest GetHeaders closure and the raw REST helpers
        // both read SupabaseConfig.Bearer per request.
        var login = await _authApi.LoginAsync(email, password);
        if (login.Outcome == AuthApi.Outcome.Ok)
        {
            SupabaseConfig.Jwt = login.Token;
            return login.User;
        }
        if (login.Outcome == AuthApi.Outcome.Denied)
            return null;

        // Fallback (fn unreachable): legacy client-side verify on the base table.
        return await ValidateLegacyAsync(email, password);
    }

    // Pre-Phase-7 path, verbatim: hash comes down, verify happens on-device.
    // Only reachable while anon can still read `users` (dies at 7b).
    private async Task<UserModel?> ValidateLegacyAsync(string email, string password)
    {
        var resp = await _supabase
            .From<UserAuthModel>()
            .Filter("email_address", Postgrest.Constants.Operator.Equals, email)
            .Get();

        var auth = resp.Models.FirstOrDefault();
        if (auth is null || auth.PasswordHash is null || auth.AccountStatus != "Activated")
            return null;

        // Driver app is for drivers only.
        if (auth.RoleId != DriverRoleId)
            return null;

        var hasher = new PasswordHasher<UserAuthModel>();
        var result = hasher.VerifyHashedPassword(auth, auth.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
            return null;

        // Full profile row via the users_app view (no hash).
        var profile = await _supabase
            .From<UserModel>()
            .Filter("user_id", Postgrest.Constants.Operator.Equals, auth.UserId.ToString())
            .Get();
        return profile.Models.FirstOrDefault();
    }
}
