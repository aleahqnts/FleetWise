using FleetWiseMobile.Models;
using Microsoft.Maui.Storage;

namespace FleetWiseMobile.Services;

// Holds the logged-in driver for the app session and persists a lightweight
// "remember me" token (user_id + Phase-7 JWT) in SecureStorage so the driver
// stays logged in across launches.
public class SessionService
{
    private const string UidKey = "fw_uid";
    private const string JwtKey = "fw_jwt";
    private readonly Supabase.Client _supabase;

    public SessionService(Supabase.Client supabase) => _supabase = supabase;

    public UserModel? CurrentUser { get; private set; }
    public bool IsLoggedIn => CurrentUser is not null;

    public string DisplayName
    {
        get
        {
            if (CurrentUser is null) return "";
            var mi = string.IsNullOrWhiteSpace(CurrentUser.MiddleName)
                ? ""
                : $" {CurrentUser.MiddleName!.Trim()[0]}.";
            return $"{CurrentUser.FirstName}{mi} {CurrentUser.LastName}".Trim();
        }
    }

    public async Task SetAsync(UserModel user)
    {
        CurrentUser = user;
        await SecureStorage.Default.SetAsync(UidKey, user.UserId.ToString());

        // Persist the driver JWT (set by AuthService on a successful edge-fn login;
        // null on the anon fallback path — then remember-me works exactly as before).
        if (SupabaseConfig.Jwt is not null)
            await SecureStorage.Default.SetAsync(JwtKey, SupabaseConfig.Jwt);
        else
            SecureStorage.Default.Remove(JwtKey);
    }

    // Re-hydrate the session from SecureStorage on app launch.
    public async Task<bool> RestoreAsync()
    {
        if (IsLoggedIn) return true;

        var uid = await SecureStorage.Default.GetAsync(UidKey);
        if (string.IsNullOrEmpty(uid)) return false;

        // Re-attach the saved JWT BEFORE the user fetch so the read itself is
        // authenticated (required once RLS flips; harmless before). The postgrest
        // GetHeaders closure picks it up from SupabaseConfig.Bearer automatically.
        var jwt = await SecureStorage.Default.GetAsync(JwtKey);
        if (!string.IsNullOrEmpty(jwt))
            SupabaseConfig.Jwt = jwt;

        try
        {
            var resp = await _supabase
                .From<UserModel>()
                .Filter("user_id", Postgrest.Constants.Operator.Equals, uid)
                .Get();
            CurrentUser = resp.Models.FirstOrDefault();
        }
        catch (Exception ex)
        {
            // Expired/revoked JWT makes PostgREST reject the request outright.
            // Treat as logged out and clear the stale token so anon paths recover.
            System.Diagnostics.Debug.WriteLine($"[Session.Restore] {ex.Message}");
            ClearJwt();
            return false;
        }
        return IsLoggedIn;
    }

    public void Logout()
    {
        CurrentUser = null;
        SecureStorage.Default.Remove(UidKey);
        ClearJwt();
    }

    private void ClearJwt()
    {
        SupabaseConfig.Jwt = null; // Bearer falls back to the anon key everywhere
        SecureStorage.Default.Remove(JwtKey);
    }
}
