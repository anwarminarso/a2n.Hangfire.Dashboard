using a2n.Hangfire.Dashboard;
using Hangfire;
using Hangfire.Console;
using Hangfire.Tags;
using SampleApp.SharedJobs;

var builder = WebApplication.CreateBuilder(args);

// Add Blazor services
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

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
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAntiforgery();

// Use Hangfire Dashboard UI at /hangfire
app.UseHangfireDashboardUI("/hangfire", new DashboardUIOptions
{
    DashboardTitle = "Sample Blazor App"
});

app.MapRazorComponents<SampleAppBlazor.Components.App>()
    .AddInteractiveServerRenderMode();

// Seed sample recurring jobs
app.Lifetime.ApplicationStarted.Register(SampleJobsSeeder.SeedMinimal);

app.Run();
