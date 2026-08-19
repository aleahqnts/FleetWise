using System.ComponentModel.DataAnnotations;
using FleetWise.Services;

namespace FleetWise.Models
{
    // Forced first-login password change (temporary -> the user's own password).
    public class ChangePasswordViewModel
    {
        [Required(ErrorMessage = "Enter a new password.")]
        [DataType(DataType.Password)]
        [StringLength(PasswordPolicy.MaxLength, MinimumLength = PasswordPolicy.MinLength,
            ErrorMessage = PasswordPolicy.LengthMessage)]
        [RegularExpression(PasswordPolicy.ComplexityPattern,
            ErrorMessage = PasswordPolicy.ComplexityMessage)]
        public string NewPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "Confirm your new password.")]
        [DataType(DataType.Password)]
        [Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
