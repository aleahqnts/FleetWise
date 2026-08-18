using FleetWiseMobile.Models;

namespace FleetWiseMobile.Services;

/// <summary>
/// Signs a driver in through the auth-login edge function, which verifies the password
/// server-side and issues a JWT valid for 30 days.
/// </summary>
/// <remarks>
/// There is no client-side fallback. Anonymous callers have no database access, so an
/// unreachable edge function means sign-in cannot proceed at all.
/// </remarks>
public class AuthService
{
    /// <summary>The temporary password new accounts start on, matching the dashboard's
    /// policy. Signing in with exactly this value means the driver has not yet chosen
    /// their own password.</summary>
    public const string TemporaryPassword = "@Temp123";

    private readonly AuthApi _authApi;

    public AuthService(AuthApi authApi) => _authApi = authApi;

    /// <returns>The user on success; null on wrong credentials.</returns>
    /// <exception cref="HttpRequestException">Edge function unreachable (offline / outage).</exception>
    public async Task<UserModel?> ValidateAsync(string email, string password)
    {
        // Setting the JWT here is enough: the postgrest header closure and the REST
        // helpers both read SupabaseConfig.Bearer on every request.
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
