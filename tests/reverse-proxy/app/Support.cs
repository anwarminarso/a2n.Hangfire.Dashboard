using a2n.Hangfire.Dashboard;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Npgsql;

namespace ProxyEnvApp;

/// <summary>Role check against the cookie principal, same as the filter described in issue #61.</summary>
public sealed class RoleDashboardAuthorizationFilter(string role) : IDashboardAuthorizationFilter
{
    public bool Authorize(HttpContext context) => context.User?.IsInRole(role) == true;
}

public sealed record DemoUser(string Name, string Password, string[] Roles);

/// <summary>Hardcoded test users. This stack is for local testing only.</summary>
public static class DemoUsers
{
    private static readonly DemoUser[] Users =
    [
        new("admin", "admin", ["HangfireAdmin"]),
        new("viewer", "viewer", []),
    ];

    public static DemoUser Find(string name, string password) =>
        Users.FirstOrDefault(u => u.Name == name && u.Password == password);
}

public class DemoJobs
{
    /// <summary>Body of seeded jobs. No retries, so seeded FailedState stays failed.</summary>
    [AutomaticRetry(Attempts = 0)]
    public void Seeded(int n) { }

    /// <summary>Short live job; ~10% fail so the failed counter moves too.</summary>
    [AutomaticRetry(Attempts = 1)]
    public async Task Quick(int n)
    {
        await Task.Delay(Random.Shared.Next(200, 1500));
        if (Random.Shared.Next(10) == 0)
            throw new InvalidOperationException($"Random failure in job #{n}");
    }

    public void Heartbeat() { }

    [Queue("reports")]
    public Task Report(string kind) => Task.Delay(3000);
}

/// <summary>
/// Seeds the job table up to Seed:TargetJobs rows (issue #61 reports ~8.1k), registers a few
/// recurring jobs, and enqueues a short job every Seed:EnqueueIntervalSeconds so realtime
/// counters keep moving.
/// </summary>
public sealed class SeedService(
    IConfiguration config,
    IBackgroundJobClient client,
    IRecurringJobManager recurring,
    IHostApplicationLifetime lifetime,
    ILogger<SeedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await WaitForStartAsync(ct);

        recurring.AddOrUpdate<DemoJobs>("heartbeat", j => j.Heartbeat(), Cron.Minutely());
        recurring.AddOrUpdate<DemoJobs>("report-every-5-min", j => j.Report("5min"), "*/5 * * * *");
        recurring.AddOrUpdate<DemoJobs>("report-hourly", j => j.Report("hourly"), Cron.Hourly());

        await SeedAsync(ct);

        var interval = config.GetValue("Seed:EnqueueIntervalSeconds", 3);
        if (interval <= 0) return;

        var n = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var id = Interlocked.Increment(ref n);
                client.Enqueue<DemoJobs>(j => j.Quick(id));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Enqueue failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
        }
    }

    private async Task SeedAsync(CancellationToken ct)
    {
        var target = config.GetValue("Seed:TargetJobs", 8000);
        if (target <= 0) return;

        long existing;
        await using (var conn = new NpgsqlConnection(config.GetConnectionString("Hangfire")))
        {
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM hangfire.job", conn);
            existing = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
        }

        var missing = (int)Math.Max(0, target - existing);
        if (missing == 0)
        {
            logger.LogInformation("Seed: job table already has {Existing} rows (target {Target})", existing, target);
            return;
        }

        logger.LogInformation("Seed: creating {Missing} jobs ({Existing} existing, target {Target})", missing, existing, target);
        var done = 0;
        await Parallel.ForAsync(0, missing, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, (i, _) =>
        {
            var n = i;
            // 80% succeeded, 10% failed, 5% scheduled, 5% deleted.
            IState state = (n % 20) switch
            {
                < 16 => new SucceededState(null, Random.Shared.Next(5, 500), Random.Shared.Next(50, 5000)),
                < 18 => new FailedState(new InvalidOperationException($"Seeded failure #{n}")),
                18 => new ScheduledState(TimeSpan.FromDays(7 + n % 30)),
                _ => new DeletedState(),
            };
            client.Create(Job.FromExpression<DemoJobs>(j => j.Seeded(n)), state);

            var count = Interlocked.Increment(ref done);
            if (count % 1000 == 0)
                logger.LogInformation("Seed: {Done}/{Missing}", count, missing);
            return ValueTask.CompletedTask;
        });
        logger.LogInformation("Seed: done ({Missing} jobs)", missing);
    }

    private Task WaitForStartAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lifetime.ApplicationStarted.Register(() => tcs.TrySetResult());
        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }
}
