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
		builder.Services.AddSingleton(supabase);

		// App services
		builder.Services.AddSingleton<Services.SessionService>();
		builder.Services.AddSingleton<Services.AuthService>();
		builder.Services.AddSingleton<Services.DriverDataService>();

		// GPS telemetry: on-device buffer + background tracker.
		builder.Services.AddSingleton<Services.TelemetryQueue>();
#if ANDROID
		builder.Services.AddSingleton<Services.ITripTracker, Platforms.Android.AndroidTripTracker>();
#else
		builder.Services.AddSingleton<Services.ITripTracker, Services.NoopTripTracker>();
#endif

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
