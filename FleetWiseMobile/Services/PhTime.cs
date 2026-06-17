namespace FleetWiseMobile.Services;

// Single source of truth for "now" in Philippine time (UTC+8, no DST).
// App is PH-only, so we store + display PH wall-clock directly — no conversion
// gymnastics, no surprises.
public static class PhTime
{
    private static readonly TimeZoneInfo Tz = Resolve();

    public static DateTime Now => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Tz).DateTime;

    // We store PH wall-clock into timestamptz columns (Postgres keeps it as +00).
    // On read, postgrest converts that to the device's LOCAL time -> shifts +8h.
    // Raw() undoes that so the UI shows the exact wall-clock that was stored.
    public static DateTime Raw(DateTime dt) =>
        dt.Kind == DateTimeKind.Unspecified ? dt : dt.ToUniversalTime();

    private static TimeZoneInfo Resolve()
    {
        foreach (var id in new[] { "Asia/Manila", "Singapore Standard Time", "Taipei Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { /* try next */ }
        }
        return TimeZoneInfo.CreateCustomTimeZone("PH", TimeSpan.FromHours(8), "PH", "PH");
    }
}
