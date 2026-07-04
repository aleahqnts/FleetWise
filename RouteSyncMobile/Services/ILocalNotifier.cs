namespace FleetWiseMobile.Services;

// Fires an OS notification while the app is running (foreground or background).
// Android shows it in the system tray; non-Android targets get the no-op impl.
public interface ILocalNotifier
{
    void Show(int id, string title, string body);
}

public class NoopLocalNotifier : ILocalNotifier
{
    public void Show(int id, string title, string body) { }
}
