using FleetWise.Models;
using Microsoft.AspNetCore.Identity;

namespace FleetWise.Data;
public static class DbSeeder
{
    public static async Task SeedAsync(Supabase.Client supabase, ILogger logger)
    {
        // Each step is independent: an INSERT failing (e.g. a desynced Postgres
        // sequence after manual seed data) shouldn't block the UPDATE-only steps.
        int? dispatcherRoleId = null;
        try
        {
            dispatcherRoleId = await EnsureDispatcherRoleAsync(supabase);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not ensure the Dispatcher role exists.");
        }

        try
        {
            await PatchPlaceholderPasswordsAsync(supabase);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not patch placeholder password hashes.");
        }

        if (dispatcherRoleId is int roleId)
        {
            try
            {
                await EnsureDispatcherUsersAsync(supabase, roleId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not ensure the Dispatcher demo users exist.");
            }
        }
    }

    private static async Task<int> EnsureDispatcherRoleAsync(Supabase.Client supabase)
    {
        var existing = await supabase.From<Role>().Get();
        var dispatcher = existing.Models.FirstOrDefault(r => r.RoleName == "Dispatcher");
        if (dispatcher is not null)
            return dispatcher.RoleId;

        var role = new Role
        {
            RoleName = "Dispatcher",
            AccessLevel = "standard",
            WebPermissions = new Dictionary<string, bool>
            {
                ["dashboard"] = true,
                ["routes"] = true,
                ["vehicles"] = true,
                ["reports"] = true,
                ["users"] = false,
            },
            MobilePermissions = new Dictionary<string, bool>
            {
                ["messages"] = true,
                ["tracking"] = true,
            },
        };

        var inserted = await supabase.From<Role>().Insert(role);
        return inserted.Models[0].RoleId;
    }

    private static async Task PatchPlaceholderPasswordsAsync(Supabase.Client supabase)
    {
        var passwords = new Dictionary<string, string>
        {
            ["admin@fleetwise.com"] = "admin123",
            ["juan@fleetwise.com"] = "driver123",
            ["maria@fleetwise.com"] = "driver123",
            ["pedro@fleetwise.com"] = "driver123",
            ["ana@fleetwise.com"] = "driver123",
        };

        var existing = await supabase.From<UserModel>().Get();
        var hasher = new PasswordHasher<UserModel>();

        foreach (var user in existing.Models)
        {
            if (user.PasswordHash != "placeholder")
                continue;

            if (user.EmailAddress is null || !passwords.TryGetValue(user.EmailAddress, out var password))
                continue;

            user.PasswordHash = hasher.HashPassword(user, password);
            user.UpdatedAt = DateTime.UtcNow;
            await supabase.From<UserModel>().Update(user);
        }
    }

    private static async Task EnsureDispatcherUsersAsync(Supabase.Client supabase, int dispatcherRoleId)
    {
        var existing = await supabase.From<UserModel>().Get();
        if (existing.Models.Any(u => u.RoleId == dispatcherRoleId))
            return;

        var hasher = new PasswordHasher<UserModel>();

        UserModel Build(string firstName, string lastName, string email)
        {
            var user = new UserModel
            {
                FirstName = firstName,
                LastName = lastName,
                EmailAddress = email,
                RoleId = dispatcherRoleId,
                AccountStatus = "Activated",
                CreatedAt = DateTime.UtcNow,
            };
            user.PasswordHash = hasher.HashPassword(user, "dispatch123");
            return user;
        }

        var dispatchers = new List<UserModel>
        {
            Build("Liza", "Fernandez", "liza.fernandez@fleetwise.com"),
            Build("Mark", "Tan", "mark.tan@fleetwise.com"),
        };

        await supabase.From<UserModel>().Insert(dispatchers);
    }
}
