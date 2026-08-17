using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FleetWise.Services
{
    // Gates a controller/action on a web permission from the signed-in user's role.
    // Permission claims ("perm") are stamped at login from roles.web_permissions; a user
    // whose role lacks the permission is bounced to the Dashboard. Pairs with the sidebar,
    // which hides the nav link for the same permission. Changing a role's permissions takes
    // effect on the user's next login (claims are issued at sign-in).
    //
    // Phase 10b: a bounce is also an audit event. The sidebar already hides the link, so
    // reaching here means someone typed the URL, and that is worth a permanent row.
    // Async filter (not IAuthorizationFilter) purely so the write can be awaited instead
    // of blocking a request thread.
    public class RequirePermissionAttribute : Attribute, IAsyncAuthorizationFilter
    {
        private readonly string _permission;
        public RequirePermissionAttribute(string permission) => _permission = permission;

        public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
        {
            var user = context.HttpContext.User;
            if (user?.Identity?.IsAuthenticated != true) return; // [Authorize] handles unauthenticated
            if (user.HasClaim("perm", _permission)) return;

            var audit = context.HttpContext.RequestServices.GetService<AuditLog>();
            if (audit is not null)
            {
                var path = context.HttpContext.Request.Path.Value ?? "";
                await audit.WriteAsync("access_denied",
                    $"was blocked from {path} (needs the {_permission} permission)",
                    outcome: "denied");
            }

            context.Result = new RedirectToActionResult("Index", "Dashboard", null);
        }
    }
}
