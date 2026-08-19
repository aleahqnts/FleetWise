using FleetWise.Models;
using static Postgrest.Constants;

namespace FleetWise.Services;

/// <summary>
/// Runtime switch for <see cref="TelemetrySimulator"/>, and cleanup of the demo data it
/// produces. Off by default, so a freshly started process never generates simulated trips
/// until an operator turns it on.
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

    // Read on every simulator tick, and marked volatile so the change is seen across threads.
    public volatile bool Enabled;

    // Serializes a simulator tick against a stop and its cleanup. Without it, a tick that
    // has already passed its enabled check can recreate a demo trip after cleanup deleted
    // one, leaving a simulated trip running while the switch is off.
    //
    // The simulator holds this for its whole tick and re-checks the switch once it has it.
    public readonly SemaphoreSlim TickGate = new(1, 1);

    public void Start() => Enabled = true;

    // Turns the producer off, then removes everything it made so only real data remains.
    // The cleanup holds the gate so it cannot race a tick mid-create. Once released,
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
    /// untagged simulated trip, which is active with no real start time.
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
            // Telemetry is deleted before the trip, so the order holds regardless of how
            // the foreign keys are configured.
            await _supabase.From<TelemetryData>().Filter("trip_id", Operator.Equals, id).Delete();
            await _supabase.From<Trip>().Filter("trip_id", Operator.Equals, id).Delete();
        }

        _logger.LogInformation("SimulatorControl cleaned {Count} simulated trips + their telemetry.", simTripIds.Count);
        return simTripIds.Count;
    }
}
