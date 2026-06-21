using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using FleetWise.Models;
using FleetWise.Services;

namespace FleetWise.Controllers
{
    [Authorize]
    public class UsersController : Controller
    {
        // Keys match the stored web_permissions / mobile_permissions JSON exactly (lowercase).
        private static readonly string[] WebPermissionKeys =
            { "dashboard", "routes", "vehicles", "reports", "users" };

        private static readonly string[] MobilePermissionKeys = { "tracking", "messages", "checklist" };

        private readonly Supabase.Client _supabase;

        public UsersController(Supabase.Client supabase) => _supabase = supabase;

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

            await _supabase.From<UserModel>().Insert(user);

            TempData["Success"] = $"User \"{model.FirstName} {model.LastName}\" created. Temporary password: {PasswordPolicy.TemporaryPassword} — they'll be asked to change it on first login.";
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

            user.FirstName = model.FirstName.Trim();
            user.MiddleName = string.IsNullOrWhiteSpace(model.MiddleName) ? null : model.MiddleName.Trim();
            user.LastName = model.LastName.Trim();
            user.EmailAddress = model.Email.Trim();
            user.RoleId = model.RoleId;
            user.AccountStatus = model.AccountStatus;
            user.UpdatedAt = PhClock.Now;

            await _supabase.From<UserModel>().Update(user);

            TempData["Success"] = $"User \"{model.FirstName} {model.LastName}\" was updated successfully.";
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

            await _supabase.From<Role>().Insert(role);

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

            var items = users
                .OrderBy(u => u.LastName)
                .ThenBy(u => u.FirstName)
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
