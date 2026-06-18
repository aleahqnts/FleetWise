namespace FleetWiseMobile.Services;

// Single source of truth for "now" in Philippine time (UTC+8, no DST).
// App is PH-only, so we store + display PH wall-clock directly — no conversion
// gymnastics, no surprises.
public static class PhTime
{
    private static readonly TimeZoneInfo Tz = Resolve();

    public static DateTime Now => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Tz).DateTime;

    // We store PH wall-clock into timestamptz columns (Postgres keeps it as +00).
    // On read, postgrest converts that to the device's LOCAL time -> shifts +8h,
    // and tags the value Local OR Unspecified (inconsistent). Raw() recovers the
    // exact wall-clock that was stored (= the original UTC components):
    //   Utc            -> already the stored wall-clock, use as-is
    //   Local/Unspec   -> value is device-local, convert back to UTC to strip +8
    public static DateTime Raw(DateTime dt) => dt.Kind == DateTimeKind.Utc
        ? dt
        : DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime();

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
