namespace FleetWiseMobile.Services;

/// <summary>
/// Starts and stops background GPS tracking for an active trip. Android runs a foreground
/// service; other platforms use a no-op implementation.
/// </summary>
public interface ITripTracker
{
    void Start(string tripId);
    void Stop();
}

/// <summary>No-op implementation, so the app still builds and runs on Windows.</summary>
public class NoopTripTracker : ITripTracker
{
    public void Start(string tripId) { }
    public void Stop() { }
}
