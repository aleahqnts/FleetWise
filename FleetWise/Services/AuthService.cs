using Microsoft.AspNetCore.Identity;
using FleetWise.Models;

namespace FleetWise.Services
{
    public class AuthService
    {
        // Drivers (role_id = 2) belong to the RouteSync mobile app, never the web dashboard —
        // mirrors the mobile AuthService, which only admits this role.
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

            // Web dashboard is for operators (admin/dispatcher) only — drivers use the app.
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
            // The web-dashboard sections this role may see (e.g. "dashboard","routes","reports").
            var permissions = role?.WebPermissions?
                .Where(kv => kv.Value).Select(kv => kv.Key).ToList() ?? new List<string>();

            return new AuthenticatedUser(
                user.UserId,
                FormatDisplayName(user.FirstName, user.MiddleName, user.LastName),
                user.EmailAddress ?? "",
                roleName,
                permissions);
        }

        // Hashes and stores a new password for the given user. Used by the forced
        // first-login change flow.
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
