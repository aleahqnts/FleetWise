using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using FleetWise.Models;
using FleetWise.Services;

namespace FleetWise.Controllers
{
    [Authorize]
    [RequirePermission("users")]
    public class UsersController : Controller
    {
        // Keys match the stored web_permissions / mobile_permissions JSON exactly (lowercase).
        private static readonly string[] WebPermissionKeys =
            { "dashboard", "routes", "vehicles", "reports", "users", "audit" };

        private static readonly string[] MobilePermissionKeys = { "tracking", "messages", "checklist" };

        private readonly Supabase.Client _supabase;
        private readonly AuditLog _audit;

        public UsersController(Supabase.Client supabase, AuditLog audit)
        {
            _supabase = supabase;
            _audit = audit;
        }

        public async Task<IActionResult> Index(string? role, string? search)
        {
            var roles = await GetRolesAsync();

            ViewBag.Roles = roles
                .Select(r => new SelectListItem { Value = r.RoleId.ToString(), Text = r.RoleName })
                .ToList();
            ViewBag.RolesFull = roles;
            ViewBag.SelectedRole = role;
            ViewBag.SearchTerm = search;
            ViewBag.AddUserModel = new AddUserViewModel();
            ViewBag.EditUserModel = new EditUserViewModel();
            ViewBag.RoleFormModel = DefaultRoleForm(roles);
            ViewBag.OpenModal = (string?)null;

            return View(new List<UserListItemViewModel>());
        }

        [HttpGet]
        public async Task<IActionResult> UserRows(string? role, string? search)
        {
            var (items, _) = await BuildUserListAsync(role, search);
            return PartialView("_UserRows", items);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(AddUserViewModel model)
        {
            if (ModelState.IsValid)
            {
                var existing = await _supabase.From<UserModel>()
                    .Filter("email_address", Postgrest.Constants.Operator.Equals, model.Email.Trim())
                    .Get();

                if (existing.Models.Count > 0)
                    ModelState.AddModelError(nameof(model.Email), "A user with this email address already exists.");
            }

            if (!ModelState.IsValid)
            {
                var result = await ReRenderIndexAsync("AddUser");
                ViewBag.AddUserModel = model;
                ViewBag.EditUserModel = new EditUserViewModel();
                return result;
            }

            var user = new UserModel
            {
                FirstName = model.FirstName.Trim(),
                MiddleName = string.IsNullOrWhiteSpace(model.MiddleName) ? null : model.MiddleName.Trim(),
                LastName = model.LastName.Trim(),
                EmailAddress = model.Email.Trim(),
                RoleId = model.RoleId,
                AccountStatus = "Activated",
                CreatedAt = PhClock.Now,
            };
            var hasher = new PasswordHasher<UserModel>();
            // Every new account starts on the shared temp password. First login forces a change.
            user.PasswordHash = hasher.HashPassword(user, PasswordPolicy.TemporaryPassword);

            var created = (await _supabase.From<UserModel>().Insert(user)).Models.FirstOrDefault();

            // A new account is a new way into the system: always worth a permanent record.
            // The temp password is public policy, not a secret, but it still never goes in.
            await _audit.WriteAsync("user_created",
                $"created the account {model.FirstName} {model.LastName} ({model.Email.Trim()})",
                "users", created?.UserId);

            TempData["Success"] = $"User \"{model.FirstName} {model.LastName}\" created. Temporary password: {PasswordPolicy.TemporaryPassword}. They'll be asked to change it on first login.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(EditUserViewModel model)
        {
            if (ModelState.IsValid)
            {
                var existing = await _supabase.From<UserModel>()
                    .Filter("email_address", Postgrest.Constants.Operator.Equals, model.Email.Trim())
                    .Get();

                if (existing.Models.Any(u => u.UserId != model.UserId))
                    ModelState.AddModelError(nameof(model.Email), "A user with this email address already exists.");
            }

            if (!ModelState.IsValid)
            {
                var result = await ReRenderIndexAsync("EditUser");
                ViewBag.AddUserModel = new AddUserViewModel();
                ViewBag.EditUserModel = model;
                return result;
            }

            var userResponse = await _supabase.From<UserModel>()
                .Filter("user_id", Postgrest.Constants.Operator.Equals, model.UserId.ToString())
                .Get();
            var user = userResponse.Models.FirstOrDefault();
            if (user is null)
            {
                TempData["Error"] = "User not found.";
                return RedirectToAction(nameof(Index));
            }

            // Kept for the audit line: a deactivation or a role swap is the part of an
            // edit that changes what this account can do.
            var wasStatus = user.AccountStatus;
            var wasRole = user.RoleId;

            user.FirstName = model.FirstName.Trim();
            user.MiddleName = string.IsNullOrWhiteSpace(model.MiddleName) ? null : model.MiddleName.Trim();
            user.LastName = model.LastName.Trim();
            user.EmailAddress = model.Email.Trim();
            user.RoleId = model.RoleId;
            user.AccountStatus = model.AccountStatus;
            user.UpdatedAt = PhClock.Now;

            await _supabase.From<UserModel>().Update(user);

            var changes = new List<string>();
            if (!string.Equals(wasStatus, model.AccountStatus, StringComparison.OrdinalIgnoreCase))
                changes.Add($"status {wasStatus} to {model.AccountStatus}");
            if (wasRole != model.RoleId)
                changes.Add("role changed");
            var detail = changes.Count > 0 ? $" ({string.Join(", ", changes)})" : "";

            await _audit.WriteAsync("user_updated",
                $"edited the account {model.FirstName} {model.LastName}{detail}",
                "users", model.UserId);

            TempData["Success"] = $"User \"{model.FirstName} {model.LastName}\" was updated successfully.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(int userId)
        {
            var userResponse = await _supabase.From<UserModel>()
                .Filter("user_id", Postgrest.Constants.Operator.Equals, userId.ToString())
                .Get();
            var user = userResponse.Models.FirstOrDefault();
            if (user is null)
            {
                TempData["Error"] = "User not found.";
                return RedirectToAction(nameof(Index));
            }

            var hasher = new PasswordHasher<UserModel>();
            // Back onto the shared temp password — same as a brand-new account, so the
            // next login with it forces a password change without any flag column.
            user.PasswordHash = hasher.HashPassword(user, PasswordPolicy.TemporaryPassword);
            user.UpdatedAt = PhClock.Now;

            await _supabase.From<UserModel>().Update(user);

            // One admin can now sign in as this person until they change it. The DB trigger
            // logs that the hash moved; this row is the one that names who did it.
            await _audit.WriteAsync("password_reset",
                $"reset the password for {user.FirstName} {user.LastName}",
                "users", userId);

            TempData["Success"] = $"Password for \"{user.FirstName} {user.LastName}\" was reset. Temporary password: {PasswordPolicy.TemporaryPassword}. They'll be asked to change it on next login.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateRole(RoleFormViewModel model)
        {
            if (ModelState.IsValid)
            {
                var existing = await _supabase.From<Role>()
                    .Filter("role_name", Postgrest.Constants.Operator.Equals, model.RoleName.Trim())
                    .Get();

                if (existing.Models.Count > 0)
                    ModelState.AddModelError(nameof(model.RoleName), "A role with this name already exists.");
            }

            if (!ModelState.IsValid)
            {
                var result = await ReRenderIndexAsync("ManageRoles");
                ViewBag.AddUserModel = new AddUserViewModel();
                ViewBag.EditUserModel = new EditUserViewModel();
                ViewBag.RoleFormModel = model;
                return result;
            }

            var role = new Role
            {
                RoleName = model.RoleName.Trim(),
                AccessLevel = model.AccessLevel.Trim(),
                WebPermissions = NormalizePermissions(model.WebPermissions, WebPermissionKeys),
                MobilePermissions = NormalizePermissions(model.MobilePermissions, MobilePermissionKeys),
            };

            var newRole = (await _supabase.From<Role>().Insert(role)).Models.FirstOrDefault();

            // A role IS the permission system. Editing one silently widens what a whole
            // group of accounts can reach, so both create and update are logged.
            await _audit.WriteAsync("role_created",
                $"created the role {role.RoleName}, dashboard access: {DescribeAccess(role.WebPermissions)}",
                "roles", newRole?.RoleId);

            TempData["Success"] = $"Role \"{model.RoleName}\" was created successfully.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateRole(RoleFormViewModel model)
        {
            if (model.RoleId is null)
                return RedirectToAction(nameof(Index));

            if (ModelState.IsValid)
            {
                var existing = await _supabase.From<Role>()
                    .Filter("role_name", Postgrest.Constants.Operator.Equals, model.RoleName.Trim())
                    .Get();

                if (existing.Models.Any(r => r.RoleId != model.RoleId))
                    ModelState.AddModelError(nameof(model.RoleName), "A role with this name already exists.");
            }

            if (!ModelState.IsValid)
            {
                var result = await ReRenderIndexAsync("ManageRoles");
                ViewBag.AddUserModel = new AddUserViewModel();
                ViewBag.EditUserModel = new EditUserViewModel();
                ViewBag.RoleFormModel = model;
                return result;
            }

            var roleResponse = await _supabase.From<Role>()
                .Filter("role_id", Postgrest.Constants.Operator.Equals, model.RoleId.Value.ToString())
                .Get();
            var role = roleResponse.Models.FirstOrDefault();
            if (role is null)
            {
                TempData["Error"] = "Role not found.";
                return RedirectToAction(nameof(Index));
            }

            role.RoleName = model.RoleName.Trim();
            role.AccessLevel = model.AccessLevel.Trim();
            role.WebPermissions = NormalizePermissions(model.WebPermissions, WebPermissionKeys);
            role.MobilePermissions = NormalizePermissions(model.MobilePermissions, MobilePermissionKeys);

            await _supabase.From<Role>().Update(role);

            await _audit.WriteAsync("role_updated",
                $"changed the role {role.RoleName}, dashboard access is now: {DescribeAccess(role.WebPermissions)}",
                "roles", role.RoleId);

            TempData["Success"] = $"Role \"{model.RoleName}\" was updated successfully.";
            return RedirectToAction(nameof(Index));
        }

        private async Task<List<Role>> GetRolesAsync()
        {
            var rolesResponse = await _supabase
                .From<Role>()
                .Order("role_name", Postgrest.Constants.Ordering.Ascending)
                .Get();

            return rolesResponse.Models;
        }

        private async Task<(List<UserListItemViewModel> Items, List<Role> Roles)> BuildUserListAsync(string? role, string? search)
        {
            var usersResponse = await _supabase.From<UserModel>().Get();
            var roles = await GetRolesAsync();

            var roleNames = roles.ToDictionary(r => r.RoleId, r => r.RoleName);

            IEnumerable<UserModel> users = usersResponse.Models;

            if (!string.IsNullOrWhiteSpace(role) && int.TryParse(role, out var roleId))
            {
                users = users.Where(u => u.RoleId == roleId);
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                users = users.Where(u =>
                    (u.FirstName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (u.LastName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (u.EmailAddress?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            // Deterministic, grouped order: by role (Admin, Dispatcher, Driver…), then
            // alphabetically by name. Case-insensitive so casing/nulls don't scramble it.
            var items = users
                .OrderBy(u => roleNames.TryGetValue(u.RoleId, out var rn) ? rn : "￿", StringComparer.OrdinalIgnoreCase)
                .ThenBy(u => u.LastName ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(u => u.FirstName ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(u => new UserListItemViewModel
                {
                    UserId = u.UserId,
                    FullName = FormatFullName(u.FirstName, u.MiddleName, u.LastName),
                    Email = u.EmailAddress ?? "",
                    RoleName = roleNames.TryGetValue(u.RoleId, out var name) ? name : "Unknown",
                    AccountStatus = u.AccountStatus ?? "Deactivated",
                    FirstName = u.FirstName ?? "",
                    MiddleName = u.MiddleName,
                    LastName = u.LastName ?? "",
                    RoleId = u.RoleId,
                })
                .ToList();

            return (items, roles);
        }

        private async Task<IActionResult> ReRenderIndexAsync(string openModal)
        {
            var roles = await GetRolesAsync();

            ViewBag.Roles = roles
                .Select(r => new SelectListItem { Value = r.RoleId.ToString(), Text = r.RoleName })
                .ToList();
            ViewBag.RolesFull = roles;
            ViewBag.SelectedRole = null;
            ViewBag.SearchTerm = null;
            ViewBag.RoleFormModel = DefaultRoleForm(roles);
            ViewBag.OpenModal = openModal;

            return View("Index", new List<UserListItemViewModel>());
        }

        // Granted sections as a readable list, for the audit line.
        private static string DescribeAccess(Dictionary<string, bool>? permissions)
        {
            var granted = permissions?.Where(kv => kv.Value).Select(kv => kv.Key).ToList() ?? new();
            return granted.Count == 0 ? "none" : string.Join(", ", granted);
        }

        private static Dictionary<string, bool> NormalizePermissions(Dictionary<string, bool> posted, string[] keys)
        {
            var result = new Dictionary<string, bool>();
            foreach (var key in keys)
                result[key] = posted.TryGetValue(key, out var value) && value;
            return result;
        }

        // The role the Manage Roles modal shows by default: the first role, with its stored
        // permissions, so the toggles open pre-filled instead of blank.
        private static RoleFormViewModel DefaultRoleForm(List<Role> roles)
        {
            var first = roles.FirstOrDefault();
            if (first is null) return new RoleFormViewModel();
            return new RoleFormViewModel
            {
                RoleId = first.RoleId,
                RoleName = first.RoleName,
                AccessLevel = first.AccessLevel,
                WebPermissions = first.WebPermissions ?? new(),
                MobilePermissions = first.MobilePermissions ?? new(),
            };
        }

        private static string FormatFullName(string? firstName, string? middleName, string? lastName)
        {
            var middleInitial = string.IsNullOrWhiteSpace(middleName) ? "" : $" {middleName.Trim()[0]}.";
            return $"{lastName}, {firstName}{middleInitial}";
        }
    }
}
