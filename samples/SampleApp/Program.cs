using a2n.Hangfire.Dashboard;
using Hangfire;
using Hangfire.Console;
using Hangfire.Tags;
using Hangfire.PostgreSql;
using SampleApp.Jobs;

var builder = WebApplication.CreateBuilder(args);

// Determine storage provider from configuration
var storageProvider = builder.Configuration["StorageProvider"] ?? "InMemory";
var sqlServerConn = builder.Configuration.GetConnectionString("SqlServer");
var postgreSqlConn = builder.Configuration.GetConnectionString("PostgreSql");

Console.WriteLine($"[SampleApp] Storage provider: {storageProvider}");

// Add Hangfire services (server + storage)
builder.Services.AddHangfire(config =>
{
    config.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
          .UseSimpleAssemblyNameTypeSerializer()
          .UseRecommendedSerializerSettings();

    switch (storageProvider)
    {
        case "SqlServer":
            config.UseSqlServerStorage(sqlServerConn);
            Console.WriteLine("[SampleApp] Using SQL Server storage");
            break;
        case "PostgreSql":
            config.UsePostgreSqlStorage(x =>
            {
                x.UseNpgsqlConnection(postgreSqlConn);
            });
            Console.WriteLine("[SampleApp] Using PostgreSQL storage");
            break;
        default:
            config.UseInMemoryStorage();
            Console.WriteLine("[SampleApp] Using InMemory storage (analytics disabled)");
            break;
    }

    config.UseConsole();
    config.UseTags();
});

builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = 2;
});

// Add Hangfire Dashboard UI services with storage adapter configuration
builder.Services.AddHangfireDashboardUI(options =>
{
    switch (storageProvider)
    {
        case "SqlServer":
            options.UseSqlServerStorage(sqlServerConn);
            break;
        case "PostgreSql":
            options.UsePostgreSqlStorage(postgreSqlConn);
            break;
        // InMemory → no adapter configured → GenericQueryProvider fallback, analytics hidden
    }
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// Use Hangfire Dashboard UI at /hangfire
app.UseHangfireDashboardUI("/hangfire", new DashboardUIOptions()
{
     DashboardTitle = "My Dashboard"
});

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

    RecurringJob.AddOrUpdate<SampleJobs>(
        "long-running-job-label",
        x => x.LongRunningJobLabel(null!),
        "*/10 * * * *");
});

app.MapGet("/health", () => Results.Ok("healthy"));

app.Run();
