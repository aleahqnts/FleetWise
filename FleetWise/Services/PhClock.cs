namespace FleetWise.Services
{
    // Philippine wall-clock time. Aleah's convention: all app-written timestamps are PH
    // wall-clock (the driver app writes Manila time), never raw UTC — so our writes must
    // match or dates/ordering drift 8 hours. PH has no DST, so it's a fixed UTC+8.
    public static class PhClock
    {
        private static readonly TimeZoneInfo Tz = ResolveTz();

        // Manila wall-clock as an Unspecified-kind DateTime, so it serializes without a
        // UTC 'Z'/offset and Postgres stores the literal local time we intend.
        public static DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Tz);

        // PH calendar date (for DATE columns like vehicles.last_maintenance_date).
        public static DateTime Today => Now.Date;

        private static TimeZoneInfo ResolveTz()
        {
            // IANA id works cross-platform on .NET 6+; Windows id is the fallback; a fixed
            // +8 offset is the last resort so the app never fails to start over a tz lookup.
            foreach (var id in new[] { "Asia/Manila", "Singapore Standard Time" })
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch { /* try next */ }
            }
            return TimeZoneInfo.CreateCustomTimeZone("PH", TimeSpan.FromHours(8), "Philippine Time", "PHT");
        }
    }
}
