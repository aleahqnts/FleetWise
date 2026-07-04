using System.Net.Http;
using System.Text;
using System.Text.Json;
using FleetWiseMobile.Models;
using static Postgrest.Constants;

namespace FleetWiseMobile.Services;

// Supabase reads/writes the driver app needs. Mirrors the patterns used by the
// web DispatchController (same tables, same status strings).
public class DriverDataService
{
    private readonly Supabase.Client _supabase;

    public DriverDataService(Supabase.Client supabase) => _supabase = supabase;

    private static readonly HttpClient _http = new();

    // Raw Supabase REST PATCH. Avoids postgrest-csharp expression Update (breaks on
    // Android) AND full-model Upsert (round-trips/corrupts the `date` column).
    private static async Task PatchAsync(string pathWithFilter, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Patch, $"{FleetWiseMobile.SupabaseConfig.Url}/rest/v1/{pathWithFilter}");
        req.Headers.TryAddWithoutValidation("apikey", FleetWiseMobile.SupabaseConfig.Key);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {FleetWiseMobile.SupabaseConfig.Key}");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var res = await _http.SendAsync(req);
        res.EnsureSuccessStatusCode();
    }

    // Raw Supabase REST POST (insert). Same Android-safe rationale as PatchAsync.
    private static async Task PostAsync(string path, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{FleetWiseMobile.SupabaseConfig.Url}/rest/v1/{path}");
        req.Headers.TryAddWithoutValidation("apikey", FleetWiseMobile.SupabaseConfig.Key);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {FleetWiseMobile.SupabaseConfig.Key}");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var res = await _http.SendAsync(req);
        res.EnsureSuccessStatusCode();
    }

    public async Task<string> GetAvailabilityAsync(int userId)
    {
        var r = await _supabase.From<DriverAvailability>()
            .Filter("user_id", Operator.Equals, userId.ToString())
            .Get();
        return r.Models.FirstOrDefault()?.AvailabilityStatus ?? "Unavailable";
    }

    public async Task SetAvailabilityAsync(int userId, string status, string? reason = null)
    {
        // Reads via postgrest-csharp are fine; WRITES go through raw REST like every other
        // write in this service — postgrest-csharp Insert/Upsert is unreliable on MAUI and
        // was silently failing here (new drivers could never flip to Available).
        var existing = await _supabase.From<DriverAvailability>()
            .Filter("user_id", Operator.Equals, userId.ToString())
            .Get();

        if (existing.Models.Any())
        {
            await PatchAsync($"driver_availability?user_id=eq.{userId}",
                new { availability_status = status, reason, updated_at = PhTime.Now });
        }
        else
        {
            await PostAsync("driver_availability",
                new { user_id = userId, availability_status = status, reason, updated_at = PhTime.Now });
        }
    }

    // Today's assignment for this driver (anything not yet completed).
    public async Task<Trip?> GetTodayAssignmentAsync(int userId)
    {
        // Include yesterday so an overnight shift (e.g. 10pm -> 6am) started on the
        // previous calendar day is still picked up after midnight.
        var yesterday = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");
        var r = await _supabase.From<Trip>()
            .Filter("driver_id", Operator.Equals, userId.ToString())
            .Filter("date", Operator.GreaterThanOrEqual, yesterday)
            .Filter("date", Operator.LessThanOrEqual, DateTime.Today.ToString("yyyy-MM-dd"))
            .Get();

        // Drop missed shifts: once shift end passes and the trip was never started,
        // it disappears. An Active trip running past its end stays (driver still on
        // it). Completed already excluded.
        var now = PhTime.Now;
        return r.Models
            .Where(t => t.TripStatus != "Completed")
            .Where(t => t.TripStatus == "Active" || now < ShiftEnd(t))
            .OrderBy(t => t.Date).ThenBy(t => t.ShiftStartTime)
            .FirstOrDefault();
    }

    // Wall-clock end of the shift. Overnight shifts (end <= start) roll to next day.
    private static DateTime ShiftEnd(Trip t)
        => t.Date.Date + t.ShiftEndTime + (t.ShiftEndTime <= t.ShiftStartTime ? TimeSpan.FromDays(1) : TimeSpan.Zero);

    // Nearest future (date > today) non-completed trip, for the Home preview.
    public async Task<Trip?> GetUpcomingAssignmentAsync(int userId)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var r = await _supabase.From<Trip>()
            .Filter("driver_id", Operator.Equals, userId.ToString())
            .Filter("date", Operator.GreaterThan, today)
            .Order("date", Ordering.Ascending)
            .Get();

        return r.Models
            .Where(t => t.TripStatus != "Completed")
            .OrderBy(t => t.Date).ThenBy(t => t.ShiftStartTime)
            .FirstOrDefault();
    }

    public async Task<BusRoute?> GetRouteAsync(int routeId)
    {
        var r = await _supabase.From<BusRoute>()
            .Filter("route_id", Operator.Equals, routeId.ToString())
            .Get();
        return r.Models.FirstOrDefault();
    }

    public async Task<Vehicle?> GetVehicleAsync(string vehicleId)
    {
        var r = await _supabase.From<Vehicle>()
            .Filter("vehicle_id", Operator.Equals, vehicleId)
            .Get();
        return r.Models.FirstOrDefault();
    }

    // Latest checklist submitted for a trip (null = none yet).
    public async Task<BusChecklist?> GetChecklistAsync(string tripId)
    {
        var r = await _supabase.From<BusChecklist>()
            .Filter("trip_id", Operator.Equals, tripId)
            .Get();
        return r.Models.OrderByDescending(c => c.SubmittedAt).FirstOrDefault();
    }

    public async Task<Trip?> GetLastCompletedTripAsync(int userId)
    {
        var r = await _supabase.From<Trip>()
            .Filter("driver_id", Operator.Equals, userId.ToString())
            .Filter("trip_status", Operator.Equals, "Completed")
            .Get();
        return r.Models.OrderByDescending(t => t.Date).FirstOrDefault();
    }

    public async Task<Trip?> GetTripAsync(string tripId)
    {
        var r = await _supabase.From<Trip>()
            .Filter("trip_id", Operator.Equals, tripId)
            .Get();
        return r.Models.FirstOrDefault();
    }

    public async Task<List<Trip>> GetTripsForDriverAsync(int userId)
    {
        var r = await _supabase.From<Trip>()
            .Filter("driver_id", Operator.Equals, userId.ToString())
            .Filter("trip_status", Operator.Equals, "Completed")
            .Order("date", Ordering.Descending)
            .Get();
        return r.Models;
    }

    // Messages the driver should see: broadcast (all) + route msgs for any route
    // the driver runs + driver-targeted msgs. Capped to the last 14 days so the
    // history stays small. Volume is tiny -> resolve route/driver match client-side.
    public async Task<List<MessageModel>> GetMessagesAsync(int userId)
    {
        var cutoff = PhTime.Now.AddDays(-14);

        // Never surface messages sent before this account existed. Without this a brand
        // new driver inherits the whole 14-day broadcast/route backlog (broadcasts match
        // everyone; route msgs match any route they're assigned to). Clamp cutoff up to
        // the account's creation time.
        var user = await GetUserAsync(userId);
        if (user is not null && user.CreatedAt > cutoff) cutoff = user.CreatedAt;

        // route ids this driver runs (all-time; small set)
        var trips = await _supabase.From<Trip>()
            .Filter("driver_id", Operator.Equals, userId.ToString())
            .Get();
        var myRoutes = trips.Models
            .Select(t => t.RouteId.ToString())
            .ToHashSet();

        var r = await _supabase.From<MessageModel>()
            .Filter("created_at", Operator.GreaterThanOrEqual, cutoff.ToString("yyyy-MM-dd HH:mm:ss"))
            .Order("created_at", Ordering.Descending)
            .Get();

        var me = userId.ToString();
        return r.Models.Where(m => (m.TargetAudience ?? "").ToLowerInvariant() switch
        {
            "all"    => true,
            "route"  => myRoutes.Contains(m.TargetId),
            "driver" => m.TargetId == me,
            _        => false
        }).ToList();
    }

    // Read state only meaningful for driver-targeted msgs (1 recipient).
    public async Task MarkMessageReadAsync(long id)
        => await PatchAsync($"messages?message_id=eq.{id}", new { is_read = true });

    public async Task<UserModel?> GetUserAsync(int userId)
    {
        var r = await _supabase.From<UserModel>()
            .Filter("user_id", Operator.Equals, userId.ToString())
            .Get();
        return r.Models.FirstOrDefault();
    }

    public async Task<UserModel?> GetDriverByEmailAsync(string email)
    {
        var r = await _supabase.From<UserModel>()
            .Filter("email_address", Operator.Equals, email)
            .Get();
        var u = r.Models.FirstOrDefault();
        return (u is not null && u.RoleId == 2 && u.AccountStatus == "Activated") ? u : null;
    }

    public async Task StampLoginAsync(int userId)
        => await PatchAsync($"users?user_id=eq.{userId}", new { last_login = PhTime.Now });

    public async Task UpdateProfileAsync(int userId, string? contact, string? address, string? emName, string? emNumber)
        => await PatchAsync($"users?user_id=eq.{userId}", new
        {
            contact_number = contact,
            address = address,
            emergency_contact_name = emName,
            emergency_contact_number = emNumber,
            updated_at = PhTime.Now
        });

    public async Task UpdatePasswordAsync(int userId, string newHash)
        => await PatchAsync($"users?user_id=eq.{userId}", new { password_hash = newHash, updated_at = PhTime.Now });

    // Returns the inserted row so the caller has the generated checklist_id (needed to
    // link a maintenance incident when the inspection fails).
    public async Task<BusChecklist?> SubmitChecklistAsync(BusChecklist checklist)
    {
        var r = await _supabase.From<BusChecklist>().Insert(checklist);
        return r.Models.FirstOrDefault();
    }

    // A failed inspection opens a maintenance incident (an unresolved maintenance_log) so the
    // flag becomes a permanent, reviewable record — surviving the bus later going On Trip —
    // instead of only flipping the volatile vehicle_status. checklist_id links it back to the
    // submitted inspection.
    public async Task OpenInspectionIncidentAsync(int checklistId, string vehicleId, string tripId, List<string> issues)
    {
        await PostAsync("maintenance_logs", new
        {
            checklist_id = checklistId,
            vehicle_id = vehicleId,
            trip_id = tripId,
            issue_details = new { issues },
            maintenance_status = "Needs Attention",
            created_at = PhTime.Now
        });
    }

    public async Task UpdateVehicleStatusAsync(string vehicleId, string status)
    {
        await PatchAsync($"vehicles?vehicle_id=eq.{Uri.EscapeDataString(vehicleId)}",
            new { vehicle_status = status, updated_at = PhTime.Now });
    }

    public async Task<decimal> GetFareAsync()
    {
        var r = await _supabase.From<FareConfig>()
            .Filter("id", Operator.Equals, "1")
            .Get();
        return r.Models.FirstOrDefault()?.StandardFare ?? 0m;
    }

    // Trip writes via raw REST PATCH (column-targeted, no `date` clobber, Android-safe).
    public async Task StartTripAsync(string tripId)
    {
        var t = await GetTripAsync(tripId);
        if (t is null) return;

        object body;
        if (t.TripStatus != "Active")
            body = new { trip_status = "Active", actual_start_time = PhTime.Now }; // fresh start
        else
            body = new { trip_status = "Active" }; // resume keeps original start

        await PatchAsync($"trips?trip_id=eq.{Uri.EscapeDataString(tripId)}", body);

        if (!string.IsNullOrEmpty(t.VehicleId))
            await UpdateVehicleStatusAsync(t.VehicleId, "On Trip");
    }

    public async Task UpdateTripProgressAsync(string tripId, int totalBoarded, decimal revenue)
    {
        await PatchAsync($"trips?trip_id=eq.{Uri.EscapeDataString(tripId)}",
            new { total_boarded = totalBoarded, estimated_revenue = revenue });
    }

    public async Task EndTripAsync(string tripId, int totalBoarded, decimal revenue)
    {
        var t = await GetTripAsync(tripId);

        await PatchAsync($"trips?trip_id=eq.{Uri.EscapeDataString(tripId)}",
            new
            {
                trip_status = "Completed",
                total_boarded = totalBoarded,
                estimated_revenue = revenue,
                actual_end_time = PhTime.Now
            });

        if (!string.IsNullOrEmpty(t?.VehicleId))
            await UpdateVehicleStatusAsync(t.VehicleId, "Ready to Deploy");
    }
}
