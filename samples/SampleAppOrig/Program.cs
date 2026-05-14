using Hangfire;
using Hangfire.Console;
using Hangfire.Tags;
using Hangfire.Tags.MemoryStorage;
using SampleAppOrig.Jobs;

var builder = WebApplication.CreateBuilder(args);

// Add Hangfire services (server + storage) with original extensions
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseInMemoryStorage()
    .UseConsole()
    .UseTagsWithMemory());

builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = 2;
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// Use the original Hangfire dashboard
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    DashboardTitle = "Hangfire Original Dashboard",
});

// Seed sample recurring jobs (identical to SampleApp)
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
