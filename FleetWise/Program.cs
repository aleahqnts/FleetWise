var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
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

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();
