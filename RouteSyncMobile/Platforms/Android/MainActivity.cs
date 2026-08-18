using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using FleetWiseMobile.Services;

namespace FleetWiseMobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private ConfirmExitCallback? _backCallback;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Android 13+ requires a runtime grant before notifications can show.
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu &&
            ContextCompat.CheckSelfPermission(this, Android.Manifest.Permission.PostNotifications)
                != Permission.Granted)
        {
            ActivityCompat.RequestPermissions(this,
                new[] { Android.Manifest.Permission.PostNotifications }, 9100);
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        // Register on first resume (after BlazorWebView wired its own back handler) so
        // OURS has top priority. We route the press through Blazor by explicit up-target
        // rather than letting the WebView walk its own history, which can hold the login
        // page and redirect bounces.
        if (_backCallback is null)
        {
            _backCallback = new ConfirmExitCallback(this);
            OnBackPressedDispatcher.AddCallback(this, _backCallback);
            // Blazor cannot background an Android task, so it asks us to.
            BackNavigation.ExitApp = () => MoveTaskToBack(true);
        }
    }

    private sealed class ConfirmExitCallback : AndroidX.Activity.OnBackPressedCallback
    {
        private readonly MainActivity _activity;
        public ConfirmExitCallback(MainActivity activity) : base(true) => _activity = activity;

        public override void HandleOnBackPressed()
        {
            // Somewhere to go up to -> go there. The prompt is only for the top of the app.
            if (BackNavigation.TryGoBack()) return;

            // The app draws its own confirmation, in the app's own colours. The system
            // dialog below is the fallback for the moment before the WebView is ready.
            if (BackNavigation.ExitPrompt?.Invoke() == true) return;

            new AndroidX.AppCompat.App.AlertDialog.Builder(_activity)
                .SetTitle("Exit RouteSync?")!
                .SetMessage("Do you want to close the app?")!
                .SetPositiveButton("Exit", (s, e) => _activity.MoveTaskToBack(true))!
                .SetNegativeButton("Cancel", (s, e) => { })!
                .Show();
        }
    }
}
