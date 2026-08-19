using Microsoft.AspNetCore.Identity;
using FleetWise.Models;

namespace FleetWise.Services
{
    public class AuthService
    {
        // Role 2 is the driver role, which belongs to the mobile app rather than the
        // dashboard. The mobile app's own sign-in admits only that role.
        private const int DriverRoleId = 2;

        private readonly Supabase.Client _supabase;

        public AuthService(Supabase.Client supabase) => _supabase = supabase;

        public async Task<AuthenticatedUser?> ValidateAsync(string email, string password)
        {
            var usersResponse = await _supabase
                .From<UserModel>()
                .Filter("email_address", Postgrest.Constants.Operator.Equals, email)
                .Get();

            var user = usersResponse.Models.FirstOrDefault();
            if (user is null || user.PasswordHash is null || user.AccountStatus != "Activated")
                return null;

            // The dashboard is for operators only. Drivers use the mobile app.
            if (user.RoleId == DriverRoleId)
                return null;

            var hasher = new PasswordHasher<UserModel>();
            var result = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
            if (result == PasswordVerificationResult.Failed)
                return null;

            var rolesResponse = await _supabase
                .From<Role>()
                .Filter("role_id", Postgrest.Constants.Operator.Equals, user.RoleId.ToString())
                .Get();

            var role = rolesResponse.Models.FirstOrDefault();
            var roleName = role?.RoleName ?? "Unknown";
            // The dashboard sections this role may see.
            var permissions = role?.WebPermissions?
                .Where(kv => kv.Value).Select(kv => kv.Key).ToList() ?? new List<string>();

            return new AuthenticatedUser(
                user.UserId,
                FormatDisplayName(user.FirstName, user.MiddleName, user.LastName),
                user.EmailAddress ?? "",
                roleName,
                permissions);
        }

        /// <summary>Hashes and stores a new password, used by the forced first-sign-in
        /// change.</summary>
        public async Task UpdatePasswordAsync(int userId, string newPassword)
        {
            var resp = await _supabase
                .From<UserModel>()
                .Filter("user_id", Postgrest.Constants.Operator.Equals, userId.ToString())
                .Get();

            var user = resp.Models.FirstOrDefault();
            if (user is null) return;

            var hasher = new PasswordHasher<UserModel>();
            user.PasswordHash = hasher.HashPassword(user, newPassword);
            user.UpdatedAt = PhClock.Now;
            await _supabase.From<UserModel>().Update(user);
        }

        private static string FormatDisplayName(string? firstName, string? middleName, string? lastName)
        {
            var middleInitial = string.IsNullOrWhiteSpace(middleName) ? "" : $" {middleName.Trim()[0]}.";
            return $"{firstName}{middleInitial} {lastName}".Trim();
        }
    }

    public record AuthenticatedUser(int UserId, string FullName, string Email, string RoleName, List<string> Permissions);
}
