using a2n.Hangfire.Dashboard;
using Hangfire;
using Hangfire.Console;
using Hangfire.Tags;
using SampleApp.Jobs;

var builder = WebApplication.CreateBuilder(args);

// Add Hangfire services (server + storage)
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseInMemoryStorage()
    .UseConsole()
    .UseTags());

builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = 2;
});

// Add Hangfire Dashboard UI services (registers Blazor, SignalR, and all internal services)
builder.Services.AddHangfireDashboardUI();

// Optional: Configure with DashboardUIOptions for authorization and custom title
// builder.Services.AddHangfireDashboardUI();
// app.UseHangfireDashboardUI("/hangfire", new DashboardUIOptions
// {
//     DashboardTitle = "My Service Dashboard",
//     DefaultRecordsPerPage = 50,
//     DefaultTheme = "auto",
//     Authorization = new[]
//     {
//         new MyDashboardAuthFilter()
//     }
// });
//
// Example authorization filter:
// public class MyDashboardAuthFilter : IDashboardAuthorizationFilter
// {
//     public bool Authorize(HttpContext context)
//     {
//         return context.User.Identity?.IsAuthenticated ?? false;
//     }
// }

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// Use Hangfire Dashboard UI at /hangfire (replaces app.UseHangfireDashboard)
app.UseHangfireDashboardUI("/hangfire");

// Seed sample recurring jobs
app.Lifetime.ApplicationStarted.Register(() =>
{
    RecurringJob.AddOrUpdate<SampleJobs>(
        "simple-job",
        x => x.SimpleJob(),
        Cron.Minutely);

    RecurringJob.AddOrUpdate<SampleJobs>(
        "console-job",
        x => x.ConsoleJob(null!),
        "*/2 * * * *");

    RecurringJob.AddOrUpdate<SampleJobs>(
        "tagged-job",
        x => x.TaggedJob(null!),
        "*/3 * * * *");

    RecurringJob.AddOrUpdate<SampleJobs>(
        "failing-job",
        x => x.FailingJob(),
        "*/5 * * * *");

    RecurringJob.AddOrUpdate<SampleJobs>(
        "long-running-job",
        x => x.LongRunningJob(null!),
        "*/10 * * * *");
});

app.MapGet("/health", () => Results.Ok("healthy"));

app.Run();
