using Microsoft.Extensions.Logging;

namespace FleetWiseMobile;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();

		// Supabase client — single shared instance (mirrors the web app's setup).
		var supabase = new Supabase.Client(SupabaseConfig.Url, SupabaseConfig.Key);
		supabase.InitializeAsync().Wait();

		// Phase 7: every postgrest read/write carries the driver JWT once login minted
		// one (SupabaseConfig.Bearer falls back to the anon key until then). Dynamic
		// closure -> login/logout flips auth for ALL From<T>() calls with no re-init.
		if (supabase.Postgrest is Postgrest.Client pg)
			pg.GetHeaders = () => new Dictionary<string, string>
			{
				["apikey"] = SupabaseConfig.Key,
				["Authorization"] = $"Bearer {SupabaseConfig.Bearer}",
			};

		builder.Services.AddSingleton(supabase);

		// App services
		builder.Services.AddSingleton<Services.AuthApi>();
		builder.Services.AddSingleton<Services.SessionService>();
		builder.Services.AddSingleton<Services.AuthService>();
		builder.Services.AddSingleton<Services.DriverDataService>();

		// GPS telemetry: on-device buffer + background tracker.
		builder.Services.AddSingleton<Services.TelemetryQueue>();
#if ANDROID
		builder.Services.AddSingleton<Services.ITripTracker, Platforms.Android.AndroidTripTracker>();
		builder.Services.AddSingleton<Services.ILocalNotifier, Platforms.Android.AndroidLocalNotifier>();
#else
		builder.Services.AddSingleton<Services.ITripTracker, Services.NoopTripTracker>();
		builder.Services.AddSingleton<Services.ILocalNotifier, Services.NoopLocalNotifier>();
#endif

		// New-message poller: badge + popup + OS notification.
		builder.Services.AddSingleton<Services.MessageWatch>();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
