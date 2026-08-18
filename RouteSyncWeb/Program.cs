using FleetWise.Data;
using FleetWise.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Phase 7: Supabase service (secret) key lives in an untracked local override so it
// never commits. Overrides Supabase:Key from appsettings.json when present. Added last,
// so it also beats the environment - a deployed host that has no file sets the env var
// Supabase__Key instead and this line quietly does nothing.
builder.Configuration.AddJsonFile("appsettings.Secret.json", optional: true, reloadOnChange: true);

// Refuse to start on the wrong key.
//
// appsettings.json ships the PUBLISHABLE key, which is fine to commit and useless to this
// server: Phase 7 took the users table away from anon, and Phase 10a took audit_log away
// too. A fresh clone with no secret file therefore booted happily and then failed in a way
// that pointed everywhere except the cause - every sign-in answered "Invalid email or
// password" on a correct password, and not one attempt reached the audit log. Cost a
// colleague an afternoon. Better to not start at all and say which file is missing.
var supabaseKey = builder.Configuration["Supabase:Key"];
if (supabaseKey is null || !supabaseKey.StartsWith("sb_secret_", StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        "Supabase:Key is not a service key, so sign-in and the audit log cannot work. " +
        "Copy RouteSyncWeb/appsettings.Secret.json.example to appsettings.Secret.json and " +
        "paste the secret key, or set the environment variable Supabase__Key when hosting. " +
        "The key is never committed, so ask for it directly.");
}

// Add services to the container.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddDefaultIdentity<IdentityUser>(options => options.SignIn.RequireConfirmedAccount = true)
    .AddEntityFrameworkStores<ApplicationDbContext>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/";
        options.AccessDeniedPath = "/";
    });

builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<FareCalculator>();

// Phase 10b: audit writer. Needs the request context to read who is signed in (the DB
// only ever sees the shared service key) and the caller's IP.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AuditLog>();

builder.Services.AddControllersWithViews();

// Tell the app how to create a Supabase connection
builder.Services.AddSingleton(provider => {
    var config = provider.GetRequiredService<IConfiguration>();

    var url = config["Supabase:Url"];  // reads from appsettings.json
    var key = config["Supabase:Key"];  // reads from appsettings.json

    var client = new Supabase.Client(url, key);
    client.InitializeAsync().Wait();   // actually opens the connection
    return client;
});

// Prunes old telemetry_data rows on a schedule so the table can't grow without bound —
// useful in every environment (real device data accrues in production too).
builder.Services.AddHostedService<TelemetryRetentionService>();

// Self-heals the shared DB: deletes ghost trips (Active + no real start + not our sim) that
// an outdated build instance leaves behind, so they never linger on the map/dashboard.
builder.Services.AddHostedService<TripReaperService>();

// Simulated live telemetry producer. Registered in every environment but OFF by default —
// SimulatorControl gates it, and an operator turns it on from the Fleet Map only when a
// demo is wanted. Turning it off deletes the data it produced.
builder.Services.AddSingleton<SimulatorControl>();
builder.Services.AddHostedService<TelemetrySimulator>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

// Security headers. The Content-Security-Policy is a backstop, not the fix: the real work
// is not putting typed values into markup in the first place. What it buys is a cap on the
// damage of any sink that gets missed later.
//
// `connect-src 'self'` is the line that matters most here. Even if an injected script runs,
// it cannot post the session or the fleet data to somebody else's server, because the
// browser refuses the request. `frame-ancestors 'none'` stops the dashboard being framed
// and clickjacked into approving something.
//
// script-src keeps 'unsafe-inline' and that is a real weakness, stated plainly: the views
// carry inline <script> blocks and onclick handlers throughout, so removing it would break
// every page until they are all refactored. Tightening it is worth doing later; shipping
// the rest of the policy now is worth more than shipping none of it.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://cdnjs.cloudflare.com https://unpkg.com; " +
        "style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://unpkg.com; " +
        "font-src 'self' data: https://cdn.jsdelivr.net; " +
        // Leaflet pulls map tiles straight from OpenStreetMap.
        "img-src 'self' data: blob: https://*.tile.openstreetmap.org https://unpkg.com; " +
        // Every call the browser makes is to this server. Supabase is talked to server-side
        // only, so the browser never needs to reach it.
        "connect-src 'self'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "object-src 'none'";
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "same-origin";
    await next();
});

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// While a user still carries the temp-password flag, lock them to the change page
// (and logout/static assets) so they can't reach the rest of the dashboard first.
app.Use(async (context, next) =>
{
    var user = context.User;
    if (user?.Identity?.IsAuthenticated == true && user.HasClaim(PasswordPolicy.MustChangeClaim, "1"))
    {
        var path = context.Request.Path.Value ?? "";
        bool isChangePage = path.StartsWith("/Home/ChangePassword", StringComparison.OrdinalIgnoreCase);
        bool isLogout = path.StartsWith("/Home/Logout", StringComparison.OrdinalIgnoreCase);
        bool isStatic = path.Contains('.');   // css/js/images carry file extensions
        if (!isChangePage && !isLogout && !isStatic)
        {
            context.Response.Redirect("/Home/ChangePassword");
            return;
        }
    }
    await next();
});

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.MapRazorPages()
   .WithStaticAssets();

app.Run();
