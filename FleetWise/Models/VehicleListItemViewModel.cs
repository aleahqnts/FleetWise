namespace FleetWise.Models
{
    public class VehicleListItemViewModel
    {
        public string VehicleId { get; set; } = string.Empty;

        public string PlateNumber { get; set; } = string.Empty;

        public string VehicleType { get; set; } = string.Empty;

        public string RouteName { get; set; } = string.Empty;

        /// <summary>Display label: Ready to Deploy / On Trip / Pending / Flagged.</summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>Derived badge: No Issues / Needs Attention / Under Repair.</summary>
        public string Maintenance { get; set; } = string.Empty;
    }
}
