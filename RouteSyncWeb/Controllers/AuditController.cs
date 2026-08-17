using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using FleetWise.Models;
using FleetWise.Services;

namespace FleetWise.Controllers
{
    // Phase 10c - the Audit Log viewer (see MOTHER-LOGS-plan.md).
    // Gated on its own "audit" permission, which no role has until it is granted
    // explicitly: read access to the trail is a separate decision from running the fleet.
    //
    // Read-only by design. There is no edit or delete action here and there never will be;
    // the table itself refuses both (10a append-only triggers).
    [Authorize]
    [RequirePermission("audit")]
    public class AuditController : Controller
    {
        private const int PageSize = 50;

        private readonly AuditLog _audit;

        public AuditController(AuditLog audit) => _audit = audit;

        // The filter param is `type`, not `action`: MVC's default route is
        // {controller}/{action}/{id?}, so a parameter named `action` binds to the route
        // value "Index" instead of the query string.
        public async Task<IActionResult> Index(
            string? type, string? source, string? outcome,
            string? q, string? from, string? to, int page = 1)
        {
            if (page < 1) page = 1;

            var filters = new List<string>
            {
                "select=*",
                "order=id.desc",                      // newest first
                $"limit={PageSize}",
                $"offset={(page - 1) * PageSize}",
            };

            if (!string.IsNullOrWhiteSpace(type))
                filters.Add($"action=eq.{Uri.EscapeDataString(type.Trim())}");
            if (!string.IsNullOrWhiteSpace(source))
                filters.Add($"source=eq.{Uri.EscapeDataString(source.Trim())}");
            if (!string.IsNullOrWhiteSpace(outcome))
                filters.Add($"outcome=eq.{Uri.EscapeDataString(outcome.Trim())}");

            // Dates are picked as PH calendar days but stored as UTC instants, so the
            // boundaries carry the +08:00 offset explicitly. "To" is inclusive of the whole
            // day, hence lt. the next midnight rather than lte. the same one.
            if (DateTime.TryParse(from, out var d1))
                filters.Add($"occurred_at=gte.{Uri.EscapeDataString($"{d1:yyyy-MM-dd}T00:00:00+08:00")}");
            if (DateTime.TryParse(to, out var d2))
                filters.Add($"occurred_at=lt.{Uri.EscapeDataString($"{d2.AddDays(1):yyyy-MM-dd}T00:00:00+08:00")}");

            var term = Sanitize(q);
            if (term.Length > 0)
            {
                var t = Uri.EscapeDataString(term);
                filters.Add($"or=(summary.ilike.*{t}*,actor_id.eq.{t},target_id.ilike.*{t}*,ip.ilike.*{t}*)");
            }

            var (rows, total) = await _audit.QueryAsync(string.Join("&", filters));

            return View(new AuditIndexViewModel
            {
                Entries = rows ?? new(),
                LoadFailed = rows is null,
                Page = page,
                PageSize = PageSize,
                Total = total,
                Type = type,
                Source = source,
                Outcome = outcome,
                Query = q,
                From = from,
                To = to,
            });
        }

        // A search term goes inside PostgREST's or=(a,b,c) list, where commas and
        // parentheses are structure. Strip them (plus the ilike wildcard) rather than try
        // to escape them, so a stray character can never reshape the query.
        private static string Sanitize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var cleaned = new string(raw.Trim()
                .Where(c => c is not (',' or '(' or ')' or '*' or '"' or '\\'))
                .ToArray());
            return cleaned.Length > 80 ? cleaned[..80] : cleaned;
        }
    }
}
