namespace FleetWiseMobile.Services;

// Bridge between the Android back button and Blazor's routing.
//
// The two live in different worlds: the hardware back press arrives on the Android
// activity, which knows nothing about which Blazor page is on screen. The activity used
// to answer every press with the exit dialog, so back never once went back - from Trip
// Details it asked you to quit the app.
//
// Deliberately NOT the WebView's own history. That history includes the login page and
// any redirect bounce, so "back" could land a signed-in driver on the sign-in screen, or
// on a page their session no longer allows. An explicit up-target per route is one line
// to read and cannot surprise anyone.
public static class BackNavigation
{
    /// <summary>Set by MainLayout while a signed-in page is on screen. Returns true if it
    /// handled the press (navigated), false when there is nowhere up and the caller should
    /// ask about exiting.</summary>
    public static Func<bool>? AppHandler { get; set; }

    /// <summary>Set by a page that owns its own back behaviour, e.g. a form with steps
    /// where back means "previous step". Takes priority over <see cref="AppHandler"/>, and
    /// the page must clear it on dispose or it will outlive the page.</summary>
    public static Func<bool>? PageHandler { get; set; }

    /// <summary>Set by BackNavHost. Shows the app's own exit confirmation and returns true
    /// if it took the press. The activity only falls back to a system AlertDialog when this
    /// is null, i.e. before the WebView has rendered anything.</summary>
    public static Func<bool>? ExitPrompt { get; set; }

    /// <summary>Set by the activity, called by the Blazor dialog's Exit button. Blazor has
    /// no way to background an Android task on its own.</summary>
    public static Action? ExitApp { get; set; }

    public static bool TryGoBack() =>
        (PageHandler?.Invoke() ?? false) || (AppHandler?.Invoke() ?? false);

    /// <summary>Where each route goes when the driver presses back. Null = already at the
    /// top, so the exit dialog is the right answer. Mirrors the on-screen back arrows the
    /// pages already draw, so hardware back and the arrow agree.</summary>
    public static string? UpFrom(string path)
    {
        // Trailing id segment, for the routes that carry a trip.
        var id = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();

        if (path.StartsWith("/trip-details/", StringComparison.Ordinal)) return "/home";
        if (path.StartsWith("/trip-report/", StringComparison.Ordinal)) return "/trips";
        if (path.StartsWith("/trip-active/", StringComparison.Ordinal)) return "/home";
        if (path.StartsWith("/checklist-log/", StringComparison.Ordinal)) return $"/trip-active/{id}";
        if (path.StartsWith("/camera-calibrate/", StringComparison.Ordinal)) return $"/trip-active/{id}";
        if (path.StartsWith("/checklist/", StringComparison.Ordinal)) return "/home";

        return path switch
        {
            "/trips" or "/notifications" or "/profile" => "/home",
            _ => null, // /home, /, /set-password: nothing above them
        };
    }
}
