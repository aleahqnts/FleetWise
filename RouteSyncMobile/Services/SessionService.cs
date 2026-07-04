using FleetWiseMobile.Models;
using Microsoft.Maui.Storage;

namespace FleetWiseMobile.Services;

// Holds the logged-in driver for the app session and persists a lightweight
// "remember me" token (user_id) in SecureStorage so the driver stays logged in
// across launches.
public class SessionService
{
    private const string UidKey = "fw_uid";
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
    }

    // Re-hydrate the session from SecureStorage on app launch.
    public async Task<bool> RestoreAsync()
    {
        if (IsLoggedIn) return true;

        var uid = await SecureStorage.Default.GetAsync(UidKey);
        if (string.IsNullOrEmpty(uid)) return false;

        var resp = await _supabase
            .From<UserModel>()
            .Filter("user_id", Postgrest.Constants.Operator.Equals, uid)
            .Get();

        CurrentUser = resp.Models.FirstOrDefault();
        return IsLoggedIn;
    }

    public void Logout()
    {
        CurrentUser = null;
        SecureStorage.Default.Remove(UidKey);
    }
}
