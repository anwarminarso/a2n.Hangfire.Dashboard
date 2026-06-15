using Hangfire;
using Hangfire.Console;
using Hangfire.PostgreSql;
using Hangfire.Tags;
using Hangfire.Tags.MemoryStorage;
using Hangfire.Tags.PostgreSql;
using Hangfire.Tags.SqlServer;
using SampleAppOrig.Jobs;

var builder = WebApplication.CreateBuilder(args);

// Determine storage provider from configuration
var storageProvider = builder.Configuration["StorageProvider"] ?? "InMemory";
var sqlServerConn = builder.Configuration.GetConnectionString("SqlServer");
var postgreSqlConn = builder.Configuration.GetConnectionString("PostgreSql");

Console.WriteLine($"[SampleAppOrig] Storage provider: {storageProvider}");

// Add Hangfire services (server + storage) with original extensions
builder.Services.AddHangfire(config =>
{
    config.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
          .UseSimpleAssemblyNameTypeSerializer()
          .UseRecommendedSerializerSettings();

    switch (storageProvider)
    {
        case "SqlServer":
            config.UseSqlServerStorage(sqlServerConn);
            config.UseConsole();
            config.UseTagsWithSql();
            // Note: FaceIT.Hangfire.Tags doesn't have a stable SqlServer storage package
            // Tags will work but won't persist across restarts
            Console.WriteLine("[SampleAppOrig] Using SQL Server storage");
            break;
        case "PostgreSql":
            config.UsePostgreSqlStorage(opts => opts.UseNpgsqlConnection(postgreSqlConn));
            config.UseConsole();
            config.UseTagsWithPostgreSql();
            // Note: FaceIT.Hangfire.Tags doesn't have a stable PostgreSql storage package
            // Tags will work but won't persist across restarts
            Console.WriteLine("[SampleAppOrig] Using PostgreSQL storage");
            break;
        default:
            config.UseInMemoryStorage();
            config.UseConsole();
            config.UseTagsWithMemory();
            Console.WriteLine("[SampleAppOrig] Using InMemory storage");
            break;
    }
});

builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = 2;
});

// Issue #10 repro: the "standard-file-transfer" recurring job is built against IFtpTransferService,
// so Hangfire's activator must be able to resolve the implementation from DI at run time.
builder.Services.AddScoped<IFtpTransferService, FtpTransferService>();

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

    RecurringJob.AddOrUpdate<SampleJobs>(
        "long-running-job-label",
        x => x.LongRunningJobLabel(null!),
        "*/10 * * * *");

    RecurringJob.AddOrUpdate<SampleJobs>(
        "pipeline-trigger",
        x => x.SeedPipeline(null!),
        "*/7 * * * *");

    RecurringJob.AddOrUpdate<IFtpTransferService>(
        "standard-file-transfer",
        x => x.StandardFileTransferServiceAsync(null!, "primary-ftp", CancellationToken.None),
        "*/15 * * * *");

});

app.MapGet("/health", () => Results.Ok("healthy"));

app.Run();
