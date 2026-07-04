using FleetWise.Models;
using static Postgrest.Constants;

namespace FleetWise.Services;

/// <summary>
/// Self-heals the shared DB against "ghost" trips — Active rows that no real driver and no
/// instance of THIS build produced. Their signature is airtight:
///
///   trip_status = "Active"  AND  actual_start_time IS NULL  AND  is_simulated = false
///
/// • A real driver trip always stamps <c>actual_start_time</c> the moment it goes Active
///   (mobile <c>StartTripAsync</c> writes both in one PATCH), so a null start on an Active
///   trip can never be a legitimate trip.
/// • Our own demo trips are tagged <c>is_simulated = true</c> and are managed by the
///   simulator's rollover / OFF-switch cleanup — excluding them here means the reaper never
///   fights the simulator over a live demo bus.
///
/// What's left is exactly the junk an OUTDATED build instance leaves on the shared DB
/// (untagged Active trips it created before the is_simulated tag / op-day rollover existed).
/// Those linger Active across days, leak into the fleet map (Active-only, no date filter)
/// and the dashboard (folds in yesterday's still-Active trips), and our code can't stop the
/// foreign process that writes them — but it CAN delete what they leave behind, every sweep,
/// so the ghosts never reach a screen for long. Telemetry is removed first, then the trip.
/// </summary>
public class TripReaperService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(2);

    private readonly Supabase.Client _supabase;
    private readonly ILogger<TripReaperService> _logger;

    public TripReaperService(Supabase.Client supabase, ILogger<TripReaperService> logger)
    {
        _supabase = supabase;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Trip reaper active: sweeping ghost trips every {Interval}.", SweepInterval);

        // Let the app finish starting before the first sweep.
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(SweepInterval);
        do
        {
            try
            {
                await SweepAsync();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // A transient Supabase hiccup must not kill the loop — log and retry next sweep.
                _logger.LogWarning(ex, "Trip reaper sweep failed; will retry next interval.");
            }
        }
        while (!stoppingToken.IsCancellationRequested &&
               await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SweepAsync()
    {
        var ghosts = (await _supabase
            .From<Trip>()
            .Filter("trip_status", Operator.Equals, "Active")
            .Get()).Models
            .Where(t => !t.IsSimulated && t.ActualStartTime is null)
            .ToList();

        if (ghosts.Count == 0)
            return;

        foreach (var trip in ghosts)
        {
            // Telemetry before the trip — no orphan rows regardless of FK setup.
            await _supabase.From<TelemetryData>().Filter("trip_id", Operator.Equals, trip.TripId).Delete();
            await _supabase.From<Trip>().Filter("trip_id", Operator.Equals, trip.TripId).Delete();

            _logger.LogInformation(
                "Reaped ghost trip {TripId} (route {RouteId}, vehicle {VehicleId}, dated {Date:yyyy-MM-dd}) + its telemetry.",
                trip.TripId, trip.RouteId, trip.VehicleId, trip.Date);
        }

        _logger.LogInformation("Trip reaper removed {Count} ghost trip(s).", ghosts.Count);
    }
}
