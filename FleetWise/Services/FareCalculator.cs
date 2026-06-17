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

        // The fleet's standard fare, now sourced from Aleah's fare_config table (single row,
        // id=1). Falls back to the appsettings rate (or 15) if the table is empty/unreachable,
        // so the map's revenue never breaks. Callers read the rate once per request, then
        // Estimate() with it for every bus.
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
