using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using FleetWise.Models;

namespace FleetWise.Controllers
{
    public class UsersController : Controller
    {
        private readonly Supabase.Client _supabase;

        public UsersController(Supabase.Client supabase) => _supabase = supabase;

        public async Task<IActionResult> Index(string? role, string? search)
        {
            var usersResponse = await _supabase.From<UserModel>().Get();
            var rolesResponse = await _supabase
                .From<Role>()
                .Order("role_name", Postgrest.Constants.Ordering.Ascending)
                .Get();

            var roles = rolesResponse.Models;
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
                })
                .ToList();

            ViewBag.Roles = roles
                .Select(r => new SelectListItem { Value = r.RoleId.ToString(), Text = r.RoleName })
                .ToList();
            ViewBag.SelectedRole = role;
            ViewBag.SearchTerm = search;

            return View(items);
        }

        private static string FormatFullName(string? firstName, string? middleName, string? lastName)
        {
            var middleInitial = string.IsNullOrWhiteSpace(middleName) ? "" : $" {middleName.Trim()[0]}.";
            return $"{lastName}, {firstName}{middleInitial}";
        }
    }
}
