using FleetWiseMobile.Models;

namespace FleetWiseMobile.Services;

// Phase 7 login (7d: JWT-only): the auth-login edge function verifies the password
// SERVER-side and mints a 30-day app_driver JWT. Anon has zero DB access, so there is
// no client-side fallback anymore — fn unreachable means login cannot proceed.
public class AuthService
{
    // Shared temp password new accounts start on (mirrors web PasswordPolicy). A login that
    // used exactly this value means the driver hasn't set their own password yet.
    public const string TemporaryPassword = "@Temp123";

    private readonly AuthApi _authApi;

    public AuthService(AuthApi authApi) => _authApi = authApi;

    /// <returns>The user on success; null on wrong credentials.</returns>
    /// <exception cref="HttpRequestException">Edge function unreachable (offline / outage).</exception>
    public async Task<UserModel?> ValidateAsync(string email, string password)
    {
        // Setting SupabaseConfig.Jwt is all it takes — the postgrest GetHeaders closure
        // and the raw REST helpers both read SupabaseConfig.Bearer per request.
        var login = await _authApi.LoginAsync(email, password);
        return login.Outcome switch
        {
            AuthApi.Outcome.Ok => Apply(login),
            AuthApi.Outcome.Denied => null,
            _ => throw new HttpRequestException("auth-login unreachable"),
        };
    }

    private static UserModel? Apply(AuthApi.LoginResult login)
    {
        SupabaseConfig.Jwt = login.Token;
        return login.User;
    }
}
