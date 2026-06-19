using FleetWise.Models;

namespace FleetWise.Services;

/// <summary>
/// Keeps the <c>telemetry_data</c> table from growing without bound. The live map only
/// reads the last ~30 min and Reports read just the latest row per trip; the Dashboard's
/// hourly passenger chart needs one operational day. Anything past the retention window is
/// deleted. Window is tunable via <c>Telemetry:RetentionMinutes</c> in configuration
/// (default 1440 = 1 day; set to 0 or below to disable the sweep).
/// </summary>
public class TelemetryRetentionService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    // Sweep cadence is derived from the window (half of it), clamped to a sane range — so a
    // short retention is actually enforced instead of waiting a full day between sweeps.
    private static readonly TimeSpan MinSweepInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxSweepInterval = TimeSpan.FromHours(12);

    private readonly Supabase.Client _supabase;
    private readonly ILogger<TelemetryRetentionService> _logger;
    private readonly int _retentionMinutes;

    public TelemetryRetentionService(
        Supabase.Client supabase,
        IConfiguration config,
        ILogger<TelemetryRetentionService> logger)
    {
        _supabase = supabase;
        _logger = logger;
        _retentionMinutes = config.GetValue<int?>("Telemetry:RetentionMinutes") ?? 1440;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_retentionMinutes <= 0)
        {
            _logger.LogInformation("Telemetry retention disabled (RetentionMinutes={Minutes}).", _retentionMinutes);
            return;
        }

        var sweepInterval = ResolveSweepInterval(_retentionMinutes);
        _logger.LogInformation(
            "Telemetry retention active: window {Minutes}m, sweeping every {Interval}.",
            _retentionMinutes, sweepInterval);

        // Let the app finish starting before the first sweep so we don't compete with
        // boot-time work.
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(sweepInterval);
        do
        {
            try
            {
                await SweepAsync();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // A transient Supabase hiccup must not kill the loop — log and retry next interval.
                _logger.LogWarning(ex, "Telemetry retention sweep failed; will retry next interval.");
            }
        }
        while (!stoppingToken.IsCancellationRequested &&
               await timer.WaitForNextTickAsync(stoppingToken));
    }

    private static TimeSpan ResolveSweepInterval(int retentionMinutes)
    {
        var half = TimeSpan.FromMinutes(retentionMinutes / 2.0);
        if (half < MinSweepInterval) return MinSweepInterval;
        if (half > MaxSweepInterval) return MaxSweepInterval;
        return half;
    }

    private async Task SweepAsync()
    {
        var cutoff = PhClock.Now.AddMinutes(-_retentionMinutes);

        // The timestamp filter is required: PostgREST rejects an unfiltered delete, and it
        // also keeps the cut strictly to rows past the retention window.
        await _supabase
            .From<TelemetryData>()
            .Filter("timestamp", Postgrest.Constants.Operator.LessThan,
                    cutoff.ToString("yyyy-MM-dd HH:mm:ss"))
            .Delete();

        _logger.LogInformation(
            "Telemetry retention sweep complete: removed rows older than {Cutoff:yyyy-MM-dd HH:mm} ({Minutes}m).",
            cutoff, _retentionMinutes);
    }
}
