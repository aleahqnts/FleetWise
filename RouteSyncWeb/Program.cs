using FleetWise.Data;
using FleetWise.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// The Supabase service key lives in an untracked local file so it never commits, and
// overrides the placeholder in appsettings.json when present. Added last, so it also takes
// precedence over the environment: a deployed host with no such file sets Supabase__Key
// instead and this line does nothing.
builder.Configuration.AddJsonFile("appsettings.Secret.json", optional: true, reloadOnChange: true);

// Refuse to start on the wrong key.
//
// appsettings.json ships the publishable key, which is safe to commit and useless to this
// server: anonymous callers have no access to the users table or the audit trail. Started
// on that key the app runs, then fails in a way that points away from the cause. Every
// sign-in answers "Invalid email or password" against a correct password, and no attempt
// reaches the audit log.
//
// Failing at startup, naming the file, is the only honest outcome.
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

// Sign-in rate limit, per caller address.
//
// The dashboard sign-in is the one door with no limit in front of it: the mobile app goes
// through an edge function that has its own. Without this, guesses are limited only by the
// network.
//
// Ten a minute is far above what a person typing a password needs and far below what makes
// guessing practical. Nothing is queued, because a caller who is over the limit should be
// told now rather than served slowly.
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("login", http => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));

    // The sign-in page reads this and explains the wait, rather than showing a bare error.
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.Redirect("/Home/Index?throttled=1");
        await Task.CompletedTask;
    };
});

// Counts failures per account, which the address limit above cannot see.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<LoginThrottle>();

// The audit writer needs the request context, both to read who is signed in, which the
// database cannot tell from the shared service key, and to record the caller's address.
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

// Prunes old telemetry on a schedule so the table cannot grow without bound. Registered in
// every environment, since real device data accumulates in production too.
builder.Services.AddHostedService<TelemetryRetentionService>();

// Closes trips a driver started but never ended, so a forgotten trip does not hold its bus
// and grow an unbounded duration.
builder.Services.AddHostedService<StaleTripCloserService>();

// Removes ghost trips left on the shared database by an outdated build, so they do not
// linger on the map or the dashboard.
builder.Services.AddHostedService<TripReaperService>();

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

// Security headers. The content security policy is a backstop rather than a fix: the real
// protection is not placing user-supplied values into markup. What the policy adds is a
// limit on the damage of anywhere that was missed.
//
// The connect-src directive is the one that matters most. Even where an injected script
// runs, it cannot send the session or fleet data to another server, because the browser
// refuses the request. The frame-ancestors directive prevents the dashboard being framed.
//
// script-src still allows inline script, which is a genuine weakness. The views carry
// inline blocks and event handlers throughout, so removing it would break every page until
// they are all rewritten.
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
        // Every call the browser makes is to this server. The database is reached only
        // from the server side, so the browser never needs to.
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

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

// A user still carrying the temporary-password claim is confined to the change page, plus
// sign-out and static assets, so they cannot reach the rest of the dashboard first.
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
