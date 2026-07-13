using System.Net.Http;
using System.Text;
using System.Text.Json;
using FleetWiseMobile.Models;
using SQLite;

namespace FleetWiseMobile.Services;

// On-device buffer for GPS telemetry. Rows are written locally first (survives
// dead zones / app kill), then a flush loop pushes them to Supabase via raw REST
// POST and deletes the local copy on success.
public class TelemetryQueue
{
    private readonly SQLiteAsyncConnection _db;
    private static readonly HttpClient _http = new();
    private static readonly SemaphoreSlim _flushLock = new(1, 1);

    public TelemetryQueue()
    {
        var path = Path.Combine(FileSystem.AppDataDirectory, "telemetry.db3");
        _db = new SQLiteAsyncConnection(path);
        _db.CreateTableAsync<PendingTelemetry>().Wait();
        _db.CreateTableAsync<PendingTripFinalize>().Wait();
    }

    public Task EnqueueAsync(PendingTelemetry row) => _db.InsertAsync(row);

    public Task EnqueueFinalizeAsync(PendingTripFinalize f) => _db.InsertAsync(f);

    public Task<int> CountAsync() => _db.Table<PendingTelemetry>().CountAsync();

    // Push buffered rows to Supabase in batches. Best-effort: stops on first
    // network failure (rows stay queued for the next flush).
    public async Task FlushAsync()
    {
        if (!await _flushLock.WaitAsync(0)) return; // a flush is already running
        try
        {
            await FlushFinalizesAsync(); // push trip totals first (audit), then GPS

            while (true)
            {
                var batch = await _db.Table<PendingTelemetry>()
                    .OrderBy(r => r.Id).Take(50).ToListAsync();
                if (batch.Count == 0) return;

                var body = batch.Select(r => new
                {
                    trip_id = r.TripId,
                    latitude = r.Latitude,
                    longitude = r.Longitude,
                    total_passengers = r.TotalPassengers,
                    speed = r.Speed,
                    heading = r.Heading,
                    timestamp = r.Timestamp
                });

                var req = new HttpRequestMessage(HttpMethod.Post,
                    $"{SupabaseConfig.Url}/rest/v1/telemetry_data");
                req.Headers.TryAddWithoutValidation("apikey", SupabaseConfig.Key);
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {SupabaseConfig.Bearer}");
                req.Headers.TryAddWithoutValidation("Prefer", "return=minimal");
                req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

                var res = await _http.SendAsync(req);
                if (!res.IsSuccessStatusCode) return; // keep rows, retry later

                var ids = batch.Select(r => r.Id).ToList();
                await _db.Table<PendingTelemetry>().DeleteAsync(r => ids.Contains(r.Id));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TelemetryQueue.Flush] {ex}");
        }
        finally { _flushLock.Release(); }
    }

    // Push queued trip finalizes -> sets trips Completed + authoritative totals.
    private async Task FlushFinalizesAsync()
    {
        var fins = await _db.Table<PendingTripFinalize>().OrderBy(f => f.Id).ToListAsync();
        foreach (var f in fins)
        {
            var body = new
            {
                trip_status = "Completed",
                total_boarded = f.TotalBoarded,
                estimated_revenue = f.Revenue,
                actual_end_time = f.EndTime
            };
            var req = new HttpRequestMessage(HttpMethod.Patch,
                $"{SupabaseConfig.Url}/rest/v1/trips?trip_id=eq.{Uri.EscapeDataString(f.TripId)}");
            req.Headers.TryAddWithoutValidation("apikey", SupabaseConfig.Key);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {SupabaseConfig.Bearer}");
            req.Headers.TryAddWithoutValidation("Prefer", "return=minimal");
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return; // keep, retry later

            // Release the bus: the trip is now Completed, so vehicle_status must leave
            // "On Trip" or the vehicles row stays stuck (the web only shows Ready via a
            // derived fallback; other consumers read the raw column). Order matters —
            // the RLS policy that lets a driver touch this vehicle needs the just-
            // written Completed trip (driver_id = me) to already exist. Best-effort:
            // a failure here doesn't re-queue the finalize (trip is already Completed).
            if (!string.IsNullOrEmpty(f.VehicleId))
            {
                var vBody = new { vehicle_status = "Ready to Deploy", updated_at = f.EndTime };
                var vReq = new HttpRequestMessage(HttpMethod.Patch,
                    $"{SupabaseConfig.Url}/rest/v1/vehicles?vehicle_id=eq.{Uri.EscapeDataString(f.VehicleId)}&vehicle_status=eq.On%20Trip");
                vReq.Headers.TryAddWithoutValidation("apikey", SupabaseConfig.Key);
                vReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {SupabaseConfig.Bearer}");
                vReq.Headers.TryAddWithoutValidation("Prefer", "return=minimal");
                vReq.Content = new StringContent(JsonSerializer.Serialize(vBody), Encoding.UTF8, "application/json");
                await _http.SendAsync(vReq); // ignore result: trip finalize already landed
            }

            await _db.DeleteAsync(f);
        }
    }
}
