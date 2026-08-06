using a2n.Hangfire.Dashboard;
using Hangfire;
using Hangfire.Console;
using Hangfire.Storage;
using Hangfire.Tags;
using SampleApp.SharedJobs;
using SampleAppSpa.Hubs;

// -----------------------------------------------------------------------------
// SampleAppSpa
// -----------------------------------------------------------------------------
// Demonstrates hosting the Hangfire Dashboard UI (a Blazor Server component)
// side-by-side with a Single Page Application (SPA).
//
// A SPA host typically:
//   * serves a static shell (wwwroot/index.html) plus JS/CSS assets
//   * does client-side routing (History API) so deep links like /jobs work
//   * uses a catch-all fallback (MapFallbackToFile / UseSpa) so ANY unmatched
//     path returns index.html and lets the client router take over
//
// That catch-all is exactly what makes SPA + a mounted sub-app tricky:
// if the fallback runs before the dashboard, every /hangfire request returns
// the SPA shell instead of the dashboard. The comments below call out the
// ordering rules that keep both working.
// -----------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

// SPA host services.
builder.Services.AddControllers();   // attribute-routed API controllers (JobsController)

// The host app's OWN SignalR. AddHangfireDashboardUI intentionally does NOT call
// AddSignalR() so the host stays in control of SignalR options. Calling it here is
// safe and additive — the dashboard's Blazor circuit reuses the same SignalR core.
builder.Services.AddSignalR();

// Add Hangfire services (InMemory storage — no external DB required for the sample)
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

// Add Hangfire Dashboard UI services (Blazor Server + SignalR are registered here)
builder.Services.AddHangfireDashboardUI();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

// -----------------------------------------------------------------------------
// IMPORTANT — ordering for SPA hosts
// -----------------------------------------------------------------------------
// UseHangfireDashboardUI mounts a *branched* pipeline via app.Map("/hangfire").
// Mount it FIRST — before UseStaticFiles, endpoint routing, and especially before
// the SPA catch-all (MapFallbackToFile below).
//
// Why "before everything"? In the minimal-hosting model a catch-all fallback such
// as MapFallbackToFile("index.html") matches EVERY path, including /hangfire. If the
// branch is registered after routing/fallback, the request is matched to the SPA
// shell and /hangfire returns index.html (you see the SPA's client-side 404 instead
// of the dashboard). Registering the branch first lets it short-circuit /hangfire/*
// requests before endpoint routing ever runs.
//
// The branch renders its own full HTML document and boots its own Blazor circuit at
// /hangfire/_blazor. It does NOT run inside the SPA shell, so the SPA's
// <base href="/"> and client-side router never touch it.
// -----------------------------------------------------------------------------
app.UseHangfireDashboardUI("/hangfire", new DashboardUIOptions
{
    DashboardTitle = "SPA Sample — Hangfire",
    EnableJobManagement = true,
    // Authorization defaults to LocalRequestsOnlyAuthorizationFilter.
});

// Serve the SPA static assets (index.html, app.js, styles.css) from wwwroot.
app.UseStaticFiles();

app.UseRouting();

// Minimal API the SPA calls to render its Home page. Kept under /api so it never
// collides with the SPA fallback or the dashboard branch.
app.MapGet("/api/stats", (JobStorage storage) =>
{
    IMonitoringApi monitor = storage.GetMonitoringApi();
    var stats = monitor.GetStatistics();
    return Results.Ok(new
    {
        enqueued = stats.Enqueued,
        scheduled = stats.Scheduled,
        processing = stats.Processing,
        succeeded = stats.Succeeded,
        failed = stats.Failed,
        recurring = stats.Recurring,
        servers = stats.Servers,
        queues = stats.Queues,
    });
});

// Host's attribute-routed controllers (GET/POST /api/jobs). These live at the root
// and never collide with the /hangfire branch.
app.MapControllers();

// Host's own SignalR hub at the root. Distinct path from the dashboard's SignalR
// endpoints (/hangfire/_blazor, /hangfire/hubs/dashboard), so there is no conflict.
app.MapHub<NotificationsHub>("/hubs/notifications");

// -----------------------------------------------------------------------------
// SPA catch-all fallback. Any request that did NOT match a static file, an API
// endpoint, a hub, or the /hangfire branch above returns index.html so the
// client-side router can handle deep links (e.g. /jobs, /about).
//
// Because the /hangfire branch already short-circuited its requests — and the hub /
// controller endpoints matched theirs — this fallback only fires for genuine SPA routes.
// -----------------------------------------------------------------------------
app.MapFallbackToFile("index.html");

// Seed a few sample recurring jobs so the dashboard has data to show.
app.Lifetime.ApplicationStarted.Register(SampleJobsSeeder.SeedMinimal);

app.Run();
