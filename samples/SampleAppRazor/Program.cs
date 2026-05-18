using a2n.Hangfire.Dashboard;
using Hangfire;
using Hangfire.Console;
using Hangfire.Tags;
using SampleAppRazor.Jobs;

var builder = WebApplication.CreateBuilder(args);

// Add Razor Pages
builder.Services.AddRazorPages();

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

// Use Hangfire Dashboard UI at /hangfire
app.UseHangfireDashboardUI("/hangfire", new DashboardUIOptions
{
    DashboardTitle = "Sample Razor Pages App"
});

app.MapRazorPages();

// Seed sample recurring jobs
app.Lifetime.ApplicationStarted.Register(() =>
{
    RecurringJob.AddOrUpdate<SampleJobs>(
        "simple-job", x => x.SimpleJob(), Cron.Minutely);

    RecurringJob.AddOrUpdate<SampleJobs>(
        "console-job", x => x.ConsoleJob(null!), "*/2 * * * *");

    RecurringJob.AddOrUpdate<SampleJobs>(
        "tagged-job", x => x.TaggedJob(null!), "*/3 * * * *");
});

app.Run();
