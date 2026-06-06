using a2n.Hangfire.Dashboard;
using Hangfire;
using Hangfire.Console;
using Hangfire.Tags;
using Hangfire.PostgreSql;
using SampleApp.SharedJobs;

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

// Use Hangfire Dashboard UI at /hangfire (default: local requests only, same as Hangfire built-in dashboard)
app.UseHangfireDashboardUI("/hangfire", new DashboardUIOptions
{
    DashboardTitle = "My Dashboard",
    EnableRecurringJobAdmin = true,

    // Make stack-trace file references clickable. While developing locally, the Local() preset
    // opens files in VS Code via the vscode:// protocol. For shared dashboards, swap to a remote
    // provider preset (GitHub / GitLab / AzureDevOps / Bitbucket) so links work for everyone.
    SourceLink = SourceLinkOptions.Local()
        .WithPathStrip("HangfireDashboard"),
    // Authorization defaults to LocalRequestsOnlyAuthorizationFilter.
    // Set Authorization = [] to allow all hosts, or see SampleAppAuth for cookie login.
});

// Seed sample recurring jobs (full demo set: basic + long-running + continuation pipeline)
app.Lifetime.ApplicationStarted.Register(SampleJobsSeeder.SeedAll);

app.MapGet("/health", () => Results.Ok("healthy"));

app.Run();
