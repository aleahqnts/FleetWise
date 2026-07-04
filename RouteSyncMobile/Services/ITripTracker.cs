namespace FleetWiseMobile.Services;

// Starts/stops background GPS tracking for an active trip. Android runs a
// foreground service; other platforms (Windows dev) get the no-op impl.
public interface ITripTracker
{
    void Start(string tripId);
    void Stop();
}

// Used on non-Android targets so the app still builds/runs on Windows.
public class NoopTripTracker : ITripTracker
{
    public void Start(string tripId) { }
    public void Stop() { }
}
