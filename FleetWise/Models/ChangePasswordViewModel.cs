using System.ComponentModel.DataAnnotations;

namespace FleetWise.Models
{
    // Forced first-login password change (temporary -> the user's own password).
    public class ChangePasswordViewModel
    {
        [Required(ErrorMessage = "Enter a new password.")]
        [DataType(DataType.Password)]
        [StringLength(100, MinimumLength = 6, ErrorMessage = "Password must be at least 6 characters.")]
        public string NewPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "Confirm your new password.")]
        [DataType(DataType.Password)]
        [Compare(nameof(NewPassword), ErrorMessage = "Passwords don't match.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
