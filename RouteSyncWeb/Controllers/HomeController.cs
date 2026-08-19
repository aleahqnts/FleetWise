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
                // The edge functions never see dashboard sign-ins, so this is the only
                // place a failed attempt at it can be recorded. The typed email is kept,
                // which is the point of the entry, but length-capped. The password never is.
                await _audit.WriteSignInAsync("login_failed",
                    $"Failed dashboard sign-in for {Attempted(model.Email)}",
                    null, "denied");

                ModelState.AddModelError("", "Invalid email or password.");
                return View(model);
            }

            // The password has already been verified. If what they typed is the shared
            // temporary password then they have never set their own, so a claim marks the
            // session and they are routed to the change page. Middleware blocks the rest of
            // the app until that is done.
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

            // Pairs with the database trigger's own entry. That one proves the hash
            // changed; this one distinguishes the account holder changing their password
            // from an administrator resetting it.
            await _audit.WriteAsync("change_password", "changed their own password",
                "users", userId);

            // The cookie is reissued without the must-change claim, which unlocks the app.
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

            // One permission claim per section the role may see. The sidebar reads them to
            // hide links, and the permission filter to block direct access.
            foreach (var p in user.Permissions ?? new List<string>())
                claims.Add(new Claim("perm", p));

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        }

        [HttpPost]
        public async Task<IActionResult> Logout()
        {
            // Recorded before the cookie is cleared, while there is still an identity to
            // name. It closes the session in the timeline: everything between the sign-in
            // entry and this one was done by that person on that machine.
            if (User?.Identity?.IsAuthenticated == true)
                await _audit.WriteAsync("logout", "signed out of the dashboard");

            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Index", "Home");
        }

        /// <summary>
        /// The email as typed on a failed attempt. Untrusted input, so it is length-capped
        /// before being stored.
        /// </summary>
        private static string Attempted(string? email)
        {
            var e = (email ?? "").Trim();
            if (e.Length == 0) return "(no email)";
            return e.Length > 120 ? e[..120] : e;
        }
    }
}
