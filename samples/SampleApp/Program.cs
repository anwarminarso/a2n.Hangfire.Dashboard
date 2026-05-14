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

// Add our alternate dashboard (option A: our options)
builder.Services.AddHangfireAlternateDashboard(new AlternateDashboardOptions
{
    DashboardTitle = "Service Job",
    DefaultRecordsPerPage = 20,
    DefaultTheme = "auto",
});

// Option B: use existing DashboardOptions for backward compat
// builder.Services.AddHangfireAlternateDashboard(new DashboardOptions
// {
//     DashboardTitle = "Service Job",
//     Authorization = new[] { new MyAuthFilter() },
// });

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// Use our alternate dashboard (replaces app.UseHangfireDashboard)
app.UseHangfireAlternateDashboard("/serviceJob");

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
