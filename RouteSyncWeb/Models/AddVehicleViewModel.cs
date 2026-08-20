using System.ComponentModel.DataAnnotations;

namespace FleetWise.Models
{
    /// <summary>
    /// The add-vehicle form. The identifier is not on it: bus numbers run in one
    /// sequence, so the registry assigns the next one rather than asking an
    /// administrator to remember which are taken.
    /// </summary>
    public class AddVehicleViewModel
    {
        [Required, StringLength(20)]
        [Display(Name = "Plate Number")]
        public string PlateNumber { get; set; } = string.Empty;

        [Required, Range(1, int.MaxValue, ErrorMessage = "Please select a route.")]
        [Display(Name = "Route")]
        public int RouteId { get; set; }
    }
}
