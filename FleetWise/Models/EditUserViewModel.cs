using System.ComponentModel.DataAnnotations;

namespace FleetWise.Models
{
    public class EditUserViewModel
    {
        [Required]
        public int UserId { get; set; }

        [Required, StringLength(50)]
        public string FirstName { get; set; } = string.Empty;

        [StringLength(50)]
        public string? MiddleName { get; set; }

        [Required, StringLength(50)]
        public string LastName { get; set; } = string.Empty;

        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required, Range(1, int.MaxValue, ErrorMessage = "Please select a role.")]
        public int RoleId { get; set; }

        [Required, RegularExpression("^(Activated|Deactivated)$")]
        public string AccountStatus { get; set; } = "Activated";
    }
}
