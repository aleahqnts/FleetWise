using System.ComponentModel.DataAnnotations;

namespace FleetWise.Models
{
    public class AddVehicleViewModel
    {
        [Required, StringLength(20)]
        [Display(Name = "Vehicle ID")]
        public string VehicleId { get; set; } = string.Empty;

        [Required, StringLength(20)]
        [Display(Name = "Plate Number")]
        public string PlateNumber { get; set; } = string.Empty;

        [Required, Range(1, int.MaxValue, ErrorMessage = "Please select a route.")]
        [Display(Name = "Route")]
        public int RouteId { get; set; }
    }
}
