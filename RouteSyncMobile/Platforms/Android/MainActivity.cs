using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using FleetWiseMobile.Services;

namespace FleetWiseMobile;

// Portrait only. The counter and the checklist are both built as a single column, and
// a phone rotating in a moving bus reflows them for no benefit.
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true,
    ScreenOrientation = ScreenOrientation.Portrait, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
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
        // Registered on the first resume, after the BlazorWebView has installed its own
        // back handler, so this one takes priority. The press is routed through Blazor by
        // explicit up-target rather than through the WebView's history, which can contain
        // the sign-in page and any redirect that bounced through it.
        if (_backCallback is null)
        {
            _backCallback = new ConfirmExitCallback(this);
            OnBackPressedDispatcher.AddCallback(this, _backCallback);
            // Blazor cannot background an Android task, so the confirmation calls back here.
            BackNavigation.ExitApp = () => MoveTaskToBack(true);
        }
    }

    private sealed class ConfirmExitCallback : AndroidX.Activity.OnBackPressedCallback
    {
        private readonly MainActivity _activity;
        public ConfirmExitCallback(MainActivity activity) : base(true) => _activity = activity;

        public override void HandleOnBackPressed()
        {
            // Navigate up where there is somewhere to go. The prompt is only for the top.
            if (BackNavigation.TryGoBack()) return;

            // The app draws its own confirmation in its own colours. The system dialog
            // below covers the window before the WebView has rendered.
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
