using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FleetWise.Services
{
    // Gates a controller/action on a web permission from the signed-in user's role.
    // Permission claims ("perm") are stamped at login from roles.web_permissions; a user
    // whose role lacks the permission is bounced to the Dashboard. Pairs with the sidebar,
    // which hides the nav link for the same permission. Changing a role's permissions takes
    // effect on the user's next login (claims are issued at sign-in).
    public class RequirePermissionAttribute : Attribute, IAuthorizationFilter
    {
        private readonly string _permission;
        public RequirePermissionAttribute(string permission) => _permission = permission;

        public void OnAuthorization(AuthorizationFilterContext context)
        {
            var user = context.HttpContext.User;
            if (user?.Identity?.IsAuthenticated != true) return; // [Authorize] handles unauthenticated
            if (!user.HasClaim("perm", _permission))
                context.Result = new RedirectToActionResult("Index", "Dashboard", null);
        }
    }
}
