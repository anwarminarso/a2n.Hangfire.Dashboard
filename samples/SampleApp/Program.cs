using a2n.Hangfire.Dashboard;
using Hangfire;
using Hangfire.Console;
using Hangfire.Tags;
using Hangfire.PostgreSql;
using Hangfire.Redis.StackExchange;
using a2n.Hangfire.Dashboard.Redis;
using SampleApp.SharedJobs;

var builder = WebApplication.CreateBuilder(args);

// Determine storage provider from configuration
var storageProvider = builder.Configuration["StorageProvider"] ?? "InMemory";
var sqlServerConn = builder.Configuration.GetConnectionString("SqlServer");
var postgreSqlConn = builder.Configuration.GetConnectionString("PostgreSql");
var redisConn = builder.Configuration.GetConnectionString("Redis");

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
        case "Redis":
            config.UseRedisStorage(redisConn, new RedisStorageOptions
            {
                Prefix = "hangfire:",
            });
            Console.WriteLine("[SampleApp] Using Redis storage (analytics disabled — no dashboard adapter)");
            break;
        default:
            config.UseInMemoryStorage();
            Console.WriteLine("[SampleApp] Using InMemory storage (analytics disabled)");
            break;
    }

    config.UseConsole();
    config.UseTags();

    // Enable the dashboard's queue-pause / maintenance-mode filter so toggling pause in the UI
    // takes effect on this Hangfire server. Without this call the dashboard still records the
    // pause but jobs would keep executing on running servers until restart.
    config.UseDashboardQueuePauseFilter();
});

builder.Services.AddHangfireServer(options =>
{
    //options.WorkerCount = 2;
});

// Issue #10 repro: the "standard-file-transfer" recurring job is built against IFtpTransferService,
// so Hangfire's activator must be able to resolve the implementation from DI at run time.
builder.Services.AddScoped<IFtpTransferService, FtpTransferService>();

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
        case "Redis":
            // Redis has no SQL query provider, so analytics/heatmap-historical are powered by
            // rollup metrics: a background collector polls succeeded/failed jobs and stores
            // aggregated rollups back in Redis. Does not open its own connection — it reuses the
            // JobStorage configured above via UseRedisStorage(...).
            options.UseRedisStorage();
            break;
        default:
            break;
            // InMemory → no adapter configured → GenericQueryProvider fallback, analytics hidden
    }
});

// Register the Hangfire dashboard as a single ASP.NET Core IHealthCheck so it shows up in the
// host's unified /health endpoint (mapped below) alongside any other dependencies. Tagged "ready"
// so you can expose a readiness-only subset via MapHealthChecks(predicate: r => r.Tags.Contains("ready")).
builder.Services.AddHealthChecks()
    .AddHangfireDashboard(tags: new[] { "ready" });

var app = builder.Build();

// Demo data for the Throttling pages. The dashboard reads Hangfire.Throttling's storage format
// directly, so the sample seeds equivalent entries without referencing that (commercial) package:
// semaphores with holders, a held mutex, and a fixed rate-limit window. Disable by setting
// SeedThrottling=false.
if (builder.Configuration["SeedThrottling"] != "false")
using (var seedScope = app.Services.CreateScope())
{
    var storage = seedScope.ServiceProvider.GetRequiredService<JobStorage>();

    // A job stuck in Processing on a server that no longer exists: demonstrates the
    // orphaned-holder detection and the detach recovery flow on the semaphore details page.
    var orphanJobId = new Hangfire.BackgroundJobClient(storage).Create(
        Hangfire.Common.Job.FromExpression(() => Console.WriteLine("orphan demo")),
        new FakeProcessingState("dead-server-01"));

    using var seedConn = storage.GetConnection();
    using var seedTx = seedConn.CreateWriteTransaction();
    foreach (var (id, max, desc) in new[] { ("globallimiter", "100", ""), ("recalculateinventory", "10", "Inventory module concurrency budget"), ("submitscrapdealertransaction", "1", ""), ("rebuildinventory", "10", "") })
    {
        seedTx.AddToSet("sync:set:sm", id);
        seedTx.SetRangeInHash($"sync:sm:{id}", new Dictionary<string, string> { ["max"] = max, ["d"] = desc });
    }
    seedTx.AddToSet("sync:j:sm:globallimiter", "13538");
    seedTx.AddToSet("sync:j:sm:globallimiter", "13558");
    seedTx.AddToSet("sync:j:sm:recalculateinventory", orphanJobId);
    seedTx.AddToSet("sync:j:sm:recalculateinventory", "13538");
    seedTx.AddToSet("sync:j:sm:submitscrapdealertransaction", "13600");
    seedTx.AddToSet("sync:set:mx", $"recalculateinventory_3ec979f5-56f7-4437-98e8-5a24d3f402a0/{orphanJobId}");
    seedTx.AddToSet($"sync:mx:recalculateinventory_3ec979f5-56f7-4437-98e8-5a24d3f402a0", orphanJobId);
    seedTx.AddToSet("sync:set:fw", "government-report-uploads");
    seedTx.SetRangeInHash("sync:fw:government-report-uploads", new Dictionary<string, string>
    {
        ["obj"] = "{\"Limit\":10,\"IntervalInSeconds\":3600,\"ActiveWindow\":123,\"Counter\":4}",
        ["d"] = "Hourly FTP upload cap",
    });
    seedTx.Commit();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// Use Hangfire Dashboard UI at /hangfire (default: local requests only, same as Hangfire built-in dashboard)
app.UseHangfireDashboardUI("/hangfire", new DashboardUIOptions
{
    DashboardTitle = "My Dashboard",

    AllowArbitraryMethodInvocation = true, // Allow invoking any method on the Job Details page (use with caution in production)
    EnableJobManagement = true, // Show "Enqueue Job"/"Create Recurring Job" buttons and allow editing existing recurring jobs in the UI

    // Recurring Schedule Heatmap: projects upcoming recurring-job fires onto a day x hour grid so you
    // can spot scheduling collisions and rebalance load. Enabled by default; shown here explicitly.
    // With a SQL Server / PostgreSQL adapter configured you also get the Historical source, Demand
    // Profile, and recommendations; on InMemory storage it runs in the storage-agnostic Projected mode.
    Heatmap = new HeatmapOptions
    {
        Enabled = true,
    },

    // Make stack-trace file references clickable. While developing locally, the Local() preset
    // opens files in VS Code via the vscode:// protocol. For shared dashboards, swap to a remote
    // provider preset (GitHub / GitLab / AzureDevOps / Bitbucket) so links work for everyone.
    SourceLink = SourceLinkOptions.Local()
        .WithPathStrip("HangfireDashboard"),

    // Health endpoints exposed inside the dashboard branch:
    //   GET /hangfire/healthz       — liveness  (storage probe only)
    //   GET /hangfire/healthz/ready — readiness (storage + servers)
    //   GET /hangfire/healthz/full  — full report (used by the Home page hero card)
    // Default = AllowAnonymous so K8s / load balancer probes work without auth.
    HealthCheckAuthorizationMode = HealthCheckAuthorization.AllowAnonymous,
    HealthCheckThresholds = new HealthThresholds
    {
        StuckProcessingMinutes = 30,
        QueueDepthWarn = 1000,
        FailureRatePercent = 10.0,
    },
    // Authorization defaults to LocalRequestsOnlyAuthorizationFilter.
    // Set Authorization = [] to allow all hosts, or see SampleAppAuth for cookie login.
});

// Seed sample recurring jobs (full demo set: basic + long-running + continuation pipeline)
app.Lifetime.ApplicationStarted.Register(SampleJobsSeeder.SeedAll);

// Two health surfaces, each with a distinct purpose:
//
//   GET /health           — host-wide aggregate via ASP.NET Core HealthChecks. Covers every
//                           registered IHealthCheck, including the Hangfire dashboard adapter
//                           (tagged "ready") wired up above. Use this for app-level probes.
//
//   GET /hangfire/healthz — Hangfire-only probe served inside the dashboard branch (liveness).
//                           Also /hangfire/healthz/ready and /hangfire/healthz/full. Use these
//                           when you want to probe Hangfire specifically without the rest of the app.
app.MapHealthChecks("/health");

app.Run();

/// <summary>
/// Demo-only state that mimics Processing on a given server (ProcessingState's constructor is
/// internal). Used to simulate a job that aborted on a dead server while holding throttling
/// primitives, so the Throttling pages can demonstrate orphan detection and detach.
/// </summary>
internal sealed class FakeProcessingState : Hangfire.States.IState
{
    private readonly string _serverId;
    public FakeProcessingState(string serverId) => _serverId = serverId;
    public string Name => Hangfire.States.ProcessingState.StateName;
    public string Reason => null;
    public bool IsFinal => false;
    public bool IgnoreJobLoadException => false;
    public Dictionary<string, string> SerializeData() => new() { ["ServerId"] = _serverId, ["WorkerId"] = "1" };
}
