using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using FleetWise.Models;
using FleetWise.Services;

namespace FleetWise.Controllers
{
    /// <summary>
    /// Read-only viewer for the audit trail.
    /// </summary>
    /// <remarks>
    /// Gated on its own permission, which no role holds until it is granted explicitly:
    /// reading the trail is a separate decision from operating the fleet.
    ///
    /// There is no edit or delete action here, and adding one would achieve nothing. The
    /// table refuses both through triggers of its own.
    /// </remarks>
    [Authorize]
    [RequirePermission("audit")]
    public class AuditController : Controller
    {
        private const int PageSize = 50;

        private readonly AuditLog _audit;

        public AuditController(AuditLog audit) => _audit = audit;

        // The filter parameter is named `type` rather than `action`, because the default
        // route pattern is {controller}/{action}/{id?}: a parameter called `action` would
        // bind to the route value instead of the query string.
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

            // Dates are chosen as Philippine calendar days but stored as UTC instants, so
            // both boundaries carry the offset explicitly. The end date includes its whole
            // day, which is why the filter is less-than the following midnight.
            if (DateTime.TryParse(from, out var d1))
                filters.Add($"occurred_at=gte.{Uri.EscapeDataString($"{d1:yyyy-MM-dd}T00:00:00+08:00")}");
            if (DateTime.TryParse(to, out var d2))
                filters.Add($"occurred_at=lt.{Uri.EscapeDataString($"{d2.AddDays(1):yyyy-MM-dd}T00:00:00+08:00")}");

            var term = Sanitize(q);
            if (term.Length > 0)
            {
                var t = Uri.EscapeDataString(term);
                // Each value is double-quoted. Inside an or-list a bare space ends the
                // value, so an unquoted "Admin User" is a parse error and the whole request
                // fails rather than returning nothing. Sanitize already removes the quote
                // character itself, so the quoting cannot be escaped from.
                filters.Add(
                    $"or=(summary.ilike.\"*{t}*\",actor_id.eq.\"{t}\"," +
                    $"target_id.ilike.\"*{t}*\",ip.ilike.\"*{t}*\")");
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

        /// <summary>
        /// Prepares a search term for use inside a PostgREST or-list.
        /// </summary>
        /// <remarks>
        /// Commas and parentheses are structural in that syntax. They are removed, along
        /// with the wildcard character, rather than escaped, so no input can reshape the
        /// query.
        /// </remarks>
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
