using FleetWise.Data;
using FleetWise.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Phase 7: Supabase service (secret) key lives in an untracked local override so it
// never commits. Overrides Supabase:Key from appsettings.json when present.
builder.Configuration.AddJsonFile("appsettings.Secret.json", optional: true, reloadOnChange: true);

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
