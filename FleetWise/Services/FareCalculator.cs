namespace FleetWise.Services
{
    public class FareCalculator
    {
        private readonly decimal _fareRate;

        public FareCalculator(IConfiguration config)
        {
            _fareRate = config.GetValue<decimal?>("FleetWise:FareRate") ?? 15.00m;
        }

        public decimal Estimate(int passengers) => passengers * _fareRate;
    }
}
