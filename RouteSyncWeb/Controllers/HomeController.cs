using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using FleetWise.Models;
using FleetWise.Services;

namespace FleetWise.Controllers
{
    [AllowAnonymous]
    public class HomeController : Controller
    {
        private readonly AuthService _authService;

        public HomeController(AuthService authService) => _authService = authService;

        public IActionResult Index()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> Index(LoginViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var user = await _authService.ValidateAsync(model.Email, model.Password);
            if (user is null)
            {
                ModelState.AddModelError("", "Invalid email or password.");
                return View(model);
            }

            // Auth already proved the hash matches; if the value they typed is the shared
            // temp password, they've never set their own -> stamp a flag claim and route them
            // through the forced change page (middleware blocks the rest of the app meanwhile).
            var mustChange = model.Password == PasswordPolicy.TemporaryPassword;
            await SignInUserAsync(user, mustChange);

            return mustChange
                ? RedirectToAction(nameof(ChangePassword))
                : RedirectToAction("Index", "Dashboard");
        }

        [Authorize]
        [HttpGet]
        public IActionResult ChangePassword() => View(new ChangePasswordViewModel());

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
        {
            if (model.NewPassword == PasswordPolicy.TemporaryPassword)
                ModelState.AddModelError(nameof(model.NewPassword), "Choose a password different from the temporary one.");

            if (!ModelState.IsValid)
                return View(model);

            var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            await _authService.UpdatePasswordAsync(userId, model.NewPassword);

            // Re-issue the cookie without the must-change flag so the app unlocks.
            var authed = new AuthenticatedUser(
                userId,
                User.FindFirstValue(ClaimTypes.Name) ?? "",
                User.FindFirstValue(ClaimTypes.Email) ?? "",
                User.FindFirstValue(ClaimTypes.Role) ?? "",
                User.FindAll("perm").Select(c => c.Value).ToList());
            await SignInUserAsync(authed, mustChange: false);

            TempData["Success"] = "Password updated. Welcome aboard!";
            return RedirectToAction("Index", "Dashboard");
        }

        private async Task SignInUserAsync(AuthenticatedUser user, bool mustChange)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
                new(ClaimTypes.Name, user.FullName),
                new(ClaimTypes.Email, user.Email),
                new(ClaimTypes.Role, user.RoleName),
            };
            if (mustChange)
                claims.Add(new Claim(PasswordPolicy.MustChangeClaim, "1"));

            // One "perm" claim per web section the role may see — read by the sidebar (hide
            // links) and RequirePermissionAttribute (block direct access).
            foreach (var p in user.Permissions ?? new List<string>())
                claims.Add(new Claim("perm", p));

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        }

        [HttpPost]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Index", "Home");
        }
    }
}
