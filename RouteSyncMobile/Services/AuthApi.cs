using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FleetWiseMobile.Models;

namespace FleetWiseMobile.Services;

/// <summary>
/// Client for the auth-login and change-password edge functions.
/// </summary>
/// <remarks>
/// These are the only paths a password travels, always over TLS, and it is verified and
/// hashed server-side.
///
/// Results are three-way rather than a boolean so a caller can tell an unreachable
/// function apart from a definitive rejection, and never treats the two the same way.
/// </remarks>
public class AuthApi
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public enum Outcome { Ok, Denied, Unreachable }

    public record LoginResult(Outcome Outcome, string? Token, UserModel? User, string? Message);
    public record CallResult(Outcome Outcome, string? Message);

    public async Task<LoginResult> LoginAsync(string email, string password)
    {
        try
        {
            var res = await PostAsync("auth-login", new { email, password });
            var body = await res.Content.ReadAsStringAsync();

            if (res.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(body);
                var token = doc.RootElement.GetProperty("token").GetString();
                var user = MapUser(doc.RootElement.GetProperty("user"));
                if (token is null || user is null)
                    return new(Outcome.Unreachable, null, null, null);
                return new(Outcome.Ok, token, user, null);
            }

            // A 400, 401 or 429 is a decision by the server, not a transport failure.
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest
                or HttpStatusCode.TooManyRequests)
                return new(Outcome.Denied, null, null, ErrorOf(body) ?? "Invalid email or password.");

            return new(Outcome.Unreachable, null, null, null); // 404/5xx: fn not there yet
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AuthApi.Login] {ex.Message}");
            return new(Outcome.Unreachable, null, null, null);
        }
    }

    public async Task<CallResult> ChangePasswordAsync(string oldPassword, string newPassword)
    {
        if (SupabaseConfig.Jwt is null) return new(Outcome.Unreachable, null);
        try
        {
            var res = await PostAsync("change-password",
                new { old_password = oldPassword, new_password = newPassword },
                SupabaseConfig.Jwt);
            var body = await res.Content.ReadAsStringAsync();

            if (res.IsSuccessStatusCode) return new(Outcome.Ok, null);
            if (res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                return new(Outcome.Denied, ErrorOf(body) ?? "Password change rejected.");
            return new(Outcome.Unreachable, null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AuthApi.ChangePwd] {ex.Message}");
            return new(Outcome.Unreachable, null);
        }
    }

    private static async Task<HttpResponseMessage> PostAsync(string fn, object body, string? jwt = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{SupabaseConfig.FunctionsUrl}/{fn}");
        req.Headers.TryAddWithoutValidation("apikey", SupabaseConfig.Key);
        if (jwt is not null)
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {jwt}");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return await _http.SendAsync(req);
    }

    private static string? ErrorOf(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        }
        catch { return null; }
    }

    // The function returns the users row in snake_case, without the password hash.
    private static UserModel? MapUser(JsonElement u)
    {
        try
        {
            return new UserModel
            {
                UserId = u.GetProperty("user_id").GetInt32(),
                FirstName = Str(u, "first_name"),
                MiddleName = Str(u, "middle_name"),
                LastName = Str(u, "last_name"),
                EmailAddress = Str(u, "email_address"),
                RoleId = u.TryGetProperty("role_id", out var r) ? r.GetInt32() : 0,
                AccountStatus = Str(u, "account_status"),
                ContactNumber = Str(u, "contact_number"),
                Address = Str(u, "address"),
                EmergencyContactName = Str(u, "emergency_contact_name"),
                EmergencyContactNumber = Str(u, "emergency_contact_number"),
                CreatedAt = Date(u, "created_at") ?? default,
                UpdatedAt = Date(u, "updated_at"),
                LastLogin = Date(u, "last_login"),
            };
        }
        catch { return null; }
    }

    private static string? Str(JsonElement u, string name)
        => u.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTime? Date(JsonElement u, string name)
        => u.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
           && DateTime.TryParse(v.GetString(), out var d) ? d : null;
}
