using FleetWise.Models;
using static Postgrest.Constants;

namespace FleetWise.Services;

/// <summary>
/// Runtime on/off switch for <see cref="TelemetrySimulator"/> plus cleanup of the demo
/// data it produces. Default is OFF, so a freshly started process never auto-generates
/// simulated trips — the simulator only runs when an operator turns it on from the UI.
/// </summary>
public class SimulatorControl
{
    private readonly Supabase.Client _supabase;
    private readonly ILogger<SimulatorControl> _logger;

    public SimulatorControl(Supabase.Client supabase, ILogger<SimulatorControl> logger)
    {
        _supabase = supabase;
        _logger = logger;
    }

    // Read every tick by the simulator. volatile so the toggle is seen across threads.
    public volatile bool Enabled;

    // Serializes a simulator tick against a stop/cleanup. Without it, a tick already past
    // its Enabled check can re-create a demo trip AFTER cleanup deleted it -> an orphan
    // sim trip survives while the switch is OFF. The simulator must take this around its
    // whole tick body and re-check Enabled once held.
    public readonly SemaphoreSlim TickGate = new(1, 1);

    public void Start() => Enabled = true;

    // Turn the producer off, then wipe everything it made so only real data remains. The
    // cleanup runs under TickGate so it can't race a tick mid-create; once it releases,
    // any waiting tick sees Enabled=false and bails before creating anything.
    public async Task<int> StopAndCleanupAsync()
    {
        Enabled = false;
        await TickGate.WaitAsync();
        try
        {
            return await CleanupSimDataAsync();
        }
        finally
        {
            TickGate.Release();
        }
    }

    /// <summary>
    /// Delete simulated trips and their telemetry. "Simulated" = tagged is_simulated, OR a
    /// legacy rogue trip (Active with no real start — the simulator's pre-tag signature).
    /// Telemetry is removed first so no orphan rows are left behind. Real phone trips
    /// (actual_start_time set) and their telemetry are never touched.
    /// </summary>
    public async Task<int> CleanupSimDataAsync()
    {
        var trips = (await _supabase.From<Trip>().Get()).Models;
        var simTripIds = trips
            .Where(t => t.IsSimulated
                     || (string.Equals(t.TripStatus, "Active", StringComparison.OrdinalIgnoreCase)
                         && t.ActualStartTime is null))
            .Select(t => t.TripId)
            .ToList();

        if (simTripIds.Count == 0)
            return 0;

        foreach (var id in simTripIds)
        {
            // Telemetry before the trip — keeps referential order regardless of FK setup.
            await _supabase.From<TelemetryData>().Filter("trip_id", Operator.Equals, id).Delete();
            await _supabase.From<Trip>().Filter("trip_id", Operator.Equals, id).Delete();
        }

        _logger.LogInformation("SimulatorControl cleaned {Count} simulated trips + their telemetry.", simTripIds.Count);
        return simTripIds.Count;
    }
}
