namespace FleetWiseMobile.Services;

/// <summary>
/// Philippine time, UTC+8 with no daylight saving, used everywhere the app needs the
/// current time.
/// </summary>
/// <remarks>
/// The system operates in one time zone, so Philippine wall-clock time is both stored and
/// displayed directly rather than converted at each boundary.
/// </remarks>
public static class PhTime
{
    private static readonly TimeZoneInfo Tz = Resolve();

    public static DateTime Now => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Tz).DateTime;

    // Philippine wall-clock time is written into timestamptz columns, which the database
    // records as +00. On the way back, postgrest converts to the device's local time,
    // adding eight hours, and tags the result as either Local or Unspecified depending on
    // the path taken.
    //
    // This recovers the wall-clock value that was actually stored:
    //   Utc              already the stored wall-clock, used as-is
    //   Local or Unspec  device-local, converted back to strip the added offset
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
