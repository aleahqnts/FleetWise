namespace FleetWiseMobile;

// Supabase connection settings.
// NOTE: this is the PUBLISHABLE (client-safe) key — it is designed to ship inside
// client apps. Do NOT put the service_role/secret key here.
public static class SupabaseConfig
{
    public const string Url = "https://vrtluruqaxutecydbrsq.supabase.co";
    public const string Key = "sb_publishable_sjkjW2K7QOPRKmixJdhSgA_8rPtoFzD";
    public const string FunctionsUrl = $"{Url}/functions/v1";

    // Phase 7: app_driver JWT minted by the auth-login edge function. Null until
    // login succeeds (or when the fn is unreachable and we fell back to anon).
    public static string? Jwt { get; set; }

    // What every REST call sends as the Authorization bearer: the driver JWT when
    // we have one, else the anon key (works until the 7d cutover kills anon).
    public static string Bearer => Jwt ?? Key;
}
