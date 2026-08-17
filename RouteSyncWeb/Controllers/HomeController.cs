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
        private readonly AuditLog _audit;

        public HomeController(AuthService authService, AuditLog audit)
        {
            _authService = authService;
            _audit = audit;
        }

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
                // The dashboard is the one door the edge functions never see, so a failed
                // attempt at it was invisible until now. The typed email is recorded (that
                // is the whole point of the row) but trimmed, and the password never is.
                await _audit.WriteSignInAsync("login_failed",
                    $"Failed dashboard sign-in for {Attempted(model.Email)}",
                    null, "denied");

                ModelState.AddModelError("", "Invalid email or password.");
                return View(model);
            }

            // Auth already proved the hash matches; if the value they typed is the shared
            // temp password, they've never set their own -> stamp a flag claim and route them
            // through the forced change page (middleware blocks the rest of the app meanwhile).
            var mustChange = model.Password == PasswordPolicy.TemporaryPassword;
            await SignInUserAsync(user, mustChange);

            await _audit.WriteSignInAsync("login",
                $"{user.FullName} ({user.Email}) signed in to the dashboard as {user.RoleName}"
                    + (mustChange ? ", still on the temporary password" : ""),
                user.UserId, role: user.RoleName);

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

            // Pairs with the DB trigger's 'password_hash_changed': that row proves the hash
            // moved, this one says the account holder did it themselves (not an admin reset).
            await _audit.WriteAsync("change_password", "changed their own password",
                "users", userId);

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
            // Logged before the cookie goes, while there is still a principal to name.
            // Closes the session in the timeline: everything between login and here was
            // done by this person on this machine.
            if (User?.Identity?.IsAuthenticated == true)
                await _audit.WriteAsync("logout", "signed out of the dashboard");

            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Index", "Home");
        }

        // The email as typed on a failed attempt: untrusted input, so it is length-capped
        // before it goes anywhere near a stored row.
        private static string Attempted(string? email)
        {
            var e = (email ?? "").Trim();
            if (e.Length == 0) return "(no email)";
            return e.Length > 120 ? e[..120] : e;
        }
    }
}
