using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FleetWise.Models;

namespace FleetWise.Services
{
    // Phase 10b - the web half of the audit trail (see MOTHER-LOGS-plan.md).
    //
    // The dashboard talks to Supabase through ONE shared service key, so every write the
    // DB triggers see arrives as `service_role` with no name on it. Which admin actually
    // clicked is known only here, in the ASP.NET cookie. This service supplies that
    // missing half: one row per admin action, actor read from the signed-in principal
    // (never from the request body, which a client could forge), source = "web".
    //
    // The pair is deliberate. The DB trigger row says WHAT changed (the before/after
    // diff); this row says WHO asked and from which IP. Same event, two witnesses.
    //
    // Rule, same as the edge writer: logging must NEVER break the action being logged.
    // Every failure is swallowed and the write is time-boxed, so an unreachable audit
    // table can slow nothing down and lock nobody out.
    public class AuditLog
    {
        private static readonly HttpClient _http = new();
        private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(3);

        private readonly IConfiguration _config;
        private readonly IHttpContextAccessor _accessor;

        public AuditLog(IConfiguration config, IHttpContextAccessor accessor)
        {
            _config = config;
            _accessor = accessor;
        }

        /// <summary>Display name of the signed-in operator, for summaries.</summary>
        public string ActorName =>
            _accessor.HttpContext?.User?.FindFirst(ClaimTypes.Name)?.Value ?? "An admin";

        /// <summary>
        /// Append one row. <paramref name="phrase"/> is the predicate only: the operator's
        /// name is prepended here so every line reads the same way ("Chester reset the
        /// password for Juan Dela Cruz"). Never pass a password, hash, or key.
        /// </summary>
        public async Task WriteAsync(
            string action,
            string phrase,
            string? targetTable = null,
            object? targetId = null,
            string outcome = "ok",
            object? changes = null)
        {
            var user = _accessor.HttpContext?.User;

            await PostAsync(new Dictionary<string, object?>
            {
                ["actor_type"] = "admin",
                ["actor_id"] = user?.FindFirst(ClaimTypes.NameIdentifier)?.Value,
                // Dashboard role (Admin, Dispatcher...), not a Postgres role. `source`
                // already tells a reader which vocabulary this column is speaking.
                ["actor_role"] = user?.FindFirst(ClaimTypes.Role)?.Value,
                ["action"] = action,
                ["target_table"] = targetTable,
                ["target_id"] = targetId?.ToString(),
                ["outcome"] = outcome,
                ["summary"] = $"{ActorName} {phrase}".Trim(),
                ["changes"] = changes,
            });
        }

        /// <summary>
        /// Append a row for a dashboard sign-in attempt. Separate from <see cref="WriteAsync"/>
        /// because at this point there may be no principal to read: a failed attempt has no
        /// identity at all, and a successful one is signed in on the RESPONSE, not yet on the
        /// request. So the caller passes what auth just established, and nothing else.
        /// Never pass the password, right or wrong.
        /// </summary>
        public async Task WriteSignInAsync(
            string action, string summary, int? userId, string outcome = "ok", string? role = null)
        {
            await PostAsync(new Dictionary<string, object?>
            {
                ["actor_type"] = userId is null ? "anon" : "admin",
                ["actor_id"] = userId?.ToString(),
                // Passed in, not read from claims: on a sign-in the principal does not exist
                // on this request yet. Without it a login row cannot tell an Admin from a
                // Dispatcher, since actor_type says "dashboard operator" for both.
                ["actor_role"] = role,
                ["action"] = action,
                ["target_table"] = "users",
                ["target_id"] = userId?.ToString(),
                ["outcome"] = outcome,
                ["summary"] = summary,
            });
        }

        /// <summary>
        /// Read the trail back for the Audit Log page (10c). <paramref name="query"/> is a
        /// PostgREST query string built by the controller. Returns null rows when the read
        /// itself failed, so the page can say so instead of showing an empty trail, which
        /// would read as "nothing ever happened".
        /// </summary>
        public async Task<(List<AuditEntryViewModel>? Rows, int Total)> QueryAsync(string query)
        {
            try
            {
                var url = _config["Supabase:Url"];
                var key = _config["Supabase:Key"];
                if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key)) return (null, 0);

                var req = new HttpRequestMessage(HttpMethod.Get, $"{url}/rest/v1/audit_log?{query}");
                req.Headers.TryAddWithoutValidation("apikey", key);
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
                // Asks PostgREST for the unpaged total, returned in Content-Range as
                // "0-49/1234". Without it there is no way to size the pager.
                req.Headers.TryAddWithoutValidation("Prefer", "count=exact");

                var res = await _http.SendAsync(req);
                if (!res.IsSuccessStatusCode) return (null, 0);

                var total = ParseTotal(res.Content.Headers.TryGetValues("Content-Range", out var cr)
                    ? cr.FirstOrDefault()
                    : null);

                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                var rows = doc.RootElement.EnumerateArray().Select(Map).ToList();
                return (rows, total == -1 ? rows.Count : total);
            }
            catch
            {
                return (null, 0);
            }
        }

        private static AuditEntryViewModel Map(JsonElement e)
        {
            static string? Str(JsonElement el, string name) =>
                el.TryGetProperty(name, out var v) && v.ValueKind is not JsonValueKind.Null
                    ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())
                    : null;

            return new AuditEntryViewModel
            {
                Id = e.TryGetProperty("id", out var id) ? id.GetInt64() : 0,
                OccurredAt = e.TryGetProperty("occurred_at", out var at)
                             && DateTimeOffset.TryParse(at.GetString(), out var parsed)
                    ? parsed
                    : DateTimeOffset.MinValue,
                ActorType = Str(e, "actor_type") ?? "",
                ActorId = Str(e, "actor_id"),
                ActorRole = Str(e, "actor_role"),
                Action = Str(e, "action") ?? "",
                TargetTable = Str(e, "target_table"),
                TargetId = Str(e, "target_id"),
                Source = Str(e, "source") ?? "",
                Outcome = Str(e, "outcome") ?? "",
                Summary = Str(e, "summary"),
                Ip = Str(e, "ip"),
                // Pretty-printed so the expanded diff is readable without a JSON viewer.
                Changes = e.TryGetProperty("changes", out var ch)
                          && ch.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                    ? JsonSerializer.Serialize(ch, new JsonSerializerOptions { WriteIndented = true })
                    : null,
            };
        }

        // "0-49/1234" -> 1234. "*" (count unknown) and anything unexpected -> -1.
        private static int ParseTotal(string? contentRange)
        {
            var slash = contentRange?.LastIndexOf('/') ?? -1;
            if (slash < 0) return -1;
            return int.TryParse(contentRange![(slash + 1)..], out var n) ? n : -1;
        }

        private async Task PostAsync(Dictionary<string, object?> row)
        {
            try
            {
                var url = _config["Supabase:Url"];
                var key = _config["Supabase:Key"];
                if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key)) return;

                row["source"] = "web";
                row["ip"] = ClientIp();

                using var cts = new CancellationTokenSource(WriteTimeout);
                var req = new HttpRequestMessage(HttpMethod.Post, $"{url}/rest/v1/audit_log");
                req.Headers.TryAddWithoutValidation("apikey", key);
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
                req.Headers.TryAddWithoutValidation("Prefer", "return=minimal");
                req.Content = new StringContent(
                    JsonSerializer.Serialize(row), Encoding.UTF8, "application/json");

                await _http.SendAsync(req, cts.Token);
            }
            catch
            {
                // Swallowed on purpose (see header).
            }
        }

        // Caller IP, best-effort. Behind a proxy the socket address is the proxy's, so
        // prefer the first hop in x-forwarded-for.
        private string? ClientIp()
        {
            var ctx = _accessor.HttpContext;
            if (ctx is null) return null;

            var fwd = ctx.Request.Headers["X-Forwarded-For"].ToString();
            if (!string.IsNullOrWhiteSpace(fwd)) return fwd.Split(',')[0].Trim();

            return ctx.Connection.RemoteIpAddress?.ToString();
        }
    }
}
