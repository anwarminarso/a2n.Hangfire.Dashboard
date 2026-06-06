using a2n.Hangfire.Dashboard;
using Hangfire;
using Hangfire.Console;
using Hangfire.Tags;
using SampleApp.SharedJobs;

var builder = WebApplication.CreateBuilder(args);

// Add MVC services
builder.Services.AddControllersWithViews();

// Add Hangfire services (InMemory storage)
builder.Services.AddHangfire(config =>
{
    config.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
          .UseSimpleAssemblyNameTypeSerializer()
          .UseRecommendedSerializerSettings()
          .UseInMemoryStorage();

    config.UseConsole();
    config.UseTags();
});

builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = 2;
});

// Add Hangfire Dashboard UI services
builder.Services.AddHangfireDashboardUI();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

// Use Hangfire Dashboard UI at /hangfire
app.UseHangfireDashboardUI("/hangfire", new DashboardUIOptions
{
    DashboardTitle = "Sample MVC App"
});

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Seed sample recurring jobs
app.Lifetime.ApplicationStarted.Register(SampleJobsSeeder.SeedMinimal);

app.Run();
