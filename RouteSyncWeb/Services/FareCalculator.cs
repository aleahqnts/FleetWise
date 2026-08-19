using FleetWise.Models;

namespace FleetWise.Services
{
    public class FareCalculator
    {
        private readonly Supabase.Client _supabase;
        private readonly decimal _fallbackRate;

        public FareCalculator(Supabase.Client supabase, IConfiguration config)
        {
            _supabase = supabase;
            _fallbackRate = config.GetValue<decimal?>("FleetWise:FareRate") ?? 15.00m;
        }

        /// <summary>
        /// The fleet's standard fare, read from configuration in the database.
        /// </summary>
        /// <remarks>
        /// Falls back to the application settings value, and then to a default, if that
        /// table is empty or unreachable, so revenue figures never fail outright.
        ///
        /// Callers read the rate once per request and reuse it for every bus.
        /// </remarks>
        public async Task<decimal> GetRateAsync()
        {
            try
            {
                var resp = await _supabase.From<FareConfig>().Get();
                if (resp.Models.FirstOrDefault()?.StandardFare is decimal fare && fare > 0)
                    return fare;
            }
            catch { /* fall through to the configured fallback */ }

            return _fallbackRate;
        }

        public decimal Estimate(int passengers, decimal rate) => passengers * rate;
    }
}
