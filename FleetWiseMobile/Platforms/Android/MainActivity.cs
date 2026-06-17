using Android.App;
using Android.Content.PM;
using Android.OS;

namespace FleetWiseMobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private ConfirmExitCallback? _backCallback;

    protected override void OnResume()
    {
        base.OnResume();
        // Register on first resume (after BlazorWebView wired its own back handler)
        // so OURS has top priority -> hardware/gesture back = exit confirm app-wide,
        // never a stale WebView history page.
        if (_backCallback is null)
        {
            _backCallback = new ConfirmExitCallback(this);
            OnBackPressedDispatcher.AddCallback(this, _backCallback);
        }
    }

    private sealed class ConfirmExitCallback : AndroidX.Activity.OnBackPressedCallback
    {
        private readonly MainActivity _activity;
        public ConfirmExitCallback(MainActivity activity) : base(true) => _activity = activity;

        public override void HandleOnBackPressed()
        {
            new AndroidX.AppCompat.App.AlertDialog.Builder(_activity)
                .SetTitle("Exit RouteSync?")!
                .SetMessage("Do you want to close the app?")!
                .SetPositiveButton("Exit", (s, e) => _activity.MoveTaskToBack(true))!
                .SetNegativeButton("Cancel", (s, e) => { })!
                .Show();
        }
    }
}
