using FleetWiseMobile.Models;
using Microsoft.Maui.Storage;

namespace FleetWiseMobile.Services;

// Background poller for driver messages. Runs for the whole logged-in session
// (own timer, independent of which page is shown). Drives three things:
//   1. unread tab badge  -> Changed event + Unread property
//   2. in-app popup       -> NewMessage event (TripActive / MainLayout subscribe)
//   3. OS notification    -> ILocalNotifier on each newly-arrived message
//
// "Unread" = driver-targeted msgs not yet read (DB is_read) PLUS broadcast/route
// msgs created after the last time the Notifications tab was opened.
public class MessageWatch
{
    private readonly DriverDataService _data;
    private readonly ILocalNotifier _notifier;

    private const string SeenTsKey = "msg_seen_ts";
    private const int PollMs = 5000;

    private System.Threading.Timer? _timer;
    private int _userId;
    private bool _seeded;
    private readonly HashSet<long> _known = new();
    private List<MessageModel> _msgs = new();
    private DateTime _seenTs = DateTime.MinValue; // cached; never block on SecureStorage

    public int Unread { get; private set; }
    public event Action? Changed;
    public event Action<MessageModel>? NewMessage;

    public MessageWatch(DriverDataService data, ILocalNotifier notifier)
    {
        _data = data;
        _notifier = notifier;
    }

    public void Start(int userId)
    {
        if (_timer is not null && _userId == userId) return; // already running
        Stop();
        _userId = userId;
        _seeded = false;
        _known.Clear();
        _ = LoadSeenTs(); // async, non-blocking
        _timer = new System.Threading.Timer(_ => _ = Poll(), null, 0, PollMs);
    }

    private async Task LoadSeenTs()
    {
        try
        {
            var s = await SecureStorage.Default.GetAsync(SeenTsKey);
            if (!string.IsNullOrEmpty(s) && DateTime.TryParse(s, out var dt)) _seenTs = dt;
        }
        catch { /* first run */ }
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _msgs = new();
        Unread = 0;
    }

    // Poll once on demand (e.g. pull-to-refresh) so badge updates immediately.
    public Task RefreshNow() => Poll();

    private async Task Poll()
    {
        if (_userId == 0) return;
        try
        {
            var msgs = await _data.GetMessagesAsync(_userId);
            _msgs = msgs;

            bool firstPass = !_seeded;
            foreach (var m in msgs)
            {
                if (_known.Contains(m.MessageId)) continue;
                _known.Add(m.MessageId);

                // First poll just seeds what already exists — don't alert per-row for history.
                if (_seeded)
                {
                    NewMessage?.Invoke(m);
                    try { _notifier.Show((int)(m.MessageId & 0x7fffffff), m.Subject ?? "New message", m.Body ?? ""); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Watch.Notify] {ex}"); }
                }
            }
            _seeded = true;

            // On app launch (first poll), surface the newest UNREAD direct message that
            // arrived while the app was closed — one popup, not a per-message storm.
            if (firstPass)
            {
                var latestUnread = msgs.FirstOrDefault(m =>
                    (m.TargetAudience ?? "").ToLowerInvariant() == "driver" && !m.IsRead);
                if (latestUnread is not null) NewMessage?.Invoke(latestUnread);
            }

            Recompute();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Watch.Poll] {ex}"); }
    }

    // Called when the Notifications tab opens: broadcast/route msgs become "seen".
    // Cutoff = max(now, newest message) so even a future-dated row is cleared.
    public void MarkSeenNow()
    {
        var cutoff = PhTime.Now;
        foreach (var m in _msgs)
        {
            var t = PhTime.Raw(m.CreatedAt);
            if (t > cutoff) cutoff = t;
        }
        _seenTs = cutoff;
        _ = PersistSeenTs(_seenTs); // fire-and-forget, never block UI
        Recompute();
    }

    private static async Task PersistSeenTs(DateTime ts)
    {
        try { await SecureStorage.Default.SetAsync(SeenTsKey, ts.ToString("o")); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Watch.Persist] {ex}"); }
    }

    // Called after a driver msg is marked read so the badge drops right away.
    public void Recompute()
    {
        int n = 0;
        foreach (var m in _msgs)
        {
            var aud = (m.TargetAudience ?? "").ToLowerInvariant();
            if (aud == "driver")
            {
                if (!m.IsRead) n++;
            }
            else // broadcast / route: no per-user read state -> use last-seen cutoff
            {
                if (PhTime.Raw(m.CreatedAt) > _seenTs) n++;
            }
        }
        Unread = n;
        Changed?.Invoke();
    }
}
