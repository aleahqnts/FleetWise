using System.ComponentModel.DataAnnotations;
using FleetWise.Services;

namespace FleetWise.Models
{
    // Step one: the address to send a code to.
    public class ForgotPasswordViewModel
    {
        [Required(ErrorMessage = "Enter your email address.")]
        [EmailAddress(ErrorMessage = "Enter a valid email address.")]
        public string Email { get; set; } = string.Empty;
    }

    // Step two: the code from the email. The address rides along in a hidden field
    // so the flow needs no server-side session before anyone has signed in.
    public class VerifyResetCodeViewModel
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Enter the 6-digit code from your email.")]
        [RegularExpression(@"^\d{6}$", ErrorMessage = "The code is 6 digits.")]
        public string Code { get; set; } = string.Empty;

        // When the last code was sent, so the page can hold the resend link shut for a
        // minute. It rides in a hidden field and is therefore only a courtesy: the
        // server's three-per-quarter-hour limit is what actually stops a flood.
        public long SentAtUnix { get; set; }
    }

    // Step three: the new password, authorised by the token the code was traded for.
    public class ResetPasswordViewModel
    {
        [Required]
        public string ResetToken { get; set; } = string.Empty;

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
