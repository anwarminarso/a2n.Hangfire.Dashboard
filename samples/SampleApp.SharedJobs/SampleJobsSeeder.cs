using Hangfire;

namespace SampleApp.SharedJobs;

/// <summary>
/// Convenience helpers for seeding the demo recurring job catalog into Hangfire.
/// Call from <c>app.Lifetime.ApplicationStarted.Register(...)</c>.
/// </summary>
public static class SampleJobsSeeder
{
    /// <summary>
    /// Registers the full demo set: simple, console, tagged, failing, long-running (×2),
    /// and the continuation pipeline trigger.
    /// </summary>
    public static void SeedAll()
    {
        SeedBasic();
        SeedLongRunning();
        SeedContinuationPipeline();
        SeedFtpTransferService();
        SeedSecondaryQueues();
    }

    /// <summary>
    /// Registers a minimal demo set (simple, console, tagged) — suitable for slim sample apps
    /// that just need representative activity in the dashboard.
    /// </summary>
    public static void SeedMinimal()
    {
        RecurringJob.AddOrUpdate<SampleJobs>("simple-job", x => x.SimpleJob(), Cron.Minutely);
        RecurringJob.AddOrUpdate<SampleJobs>("console-job", x => x.ConsoleJob(null!), "*/2 * * * *");
        RecurringJob.AddOrUpdate<SampleJobs>("tagged-job", x => x.TaggedJob(null!), "*/3 * * * *");
    }

    /// <summary>
    /// Registers simple, console, tagged, and failing jobs.
    /// </summary>
    public static void SeedBasic()
    {
        SeedMinimal();
        RecurringJob.AddOrUpdate<SampleJobs>("failing-job", x => x.FailingJob(), "*/5 * * * *");
    }

    /// <summary>
    /// Registers both long-running jobs (with and without a custom display name).
    /// </summary>
    public static void SeedLongRunning()
    {
        RecurringJob.AddOrUpdate<SampleJobs>("long-running-job", x => x.LongRunningJob(null!), "*/10 * * * *");
        RecurringJob.AddOrUpdate<SampleJobs>("long-running-job-label", x => x.LongRunningJobLabel(null!), "*/10 * * * *");
    }

    /// <summary>
    /// Registers a recurring trigger that fires the Extract → Transform → Load → Notify pipeline.
    /// Demonstrates the Job Dependency Graph viewer on the Job Details page.
    /// </summary>
    public static void SeedContinuationPipeline()
    {
        RecurringJob.AddOrUpdate<SampleJobs>("pipeline-trigger", x => x.SeedPipeline(null!), "*/7 * * * *");
    }

    /// <summary>
    /// Registers the issue #10 repro: a recurring job built against the <see cref="IFtpTransferService"/>
    /// interface (resolved from DI at run time) whose method takes a user parameter (<c>ftpName</c>)
    /// between injected <c>PerformContext</c> and <c>CancellationToken</c>. Opening this job in the
    /// recurring edit form exercises the argument-deserialisation fix (the form should pre-fill
    /// <c>ftpName</c> and ignore the injected parameters).
    /// </summary>
    public static void SeedFtpTransferService()
    {
        RecurringJob.AddOrUpdate<IFtpTransferService>(
            "standard-file-transfer",
            x => x.StandardFileTransferServiceAsync(null!, "primary-ftp", CancellationToken.None),
            "*/15 * * * *");
    }

    /// <summary>
    /// Registers recurring jobs on non-default queues (<c>reports</c> and <c>maintenance</c>) so the
    /// Schedule Heatmap's multi-queue filtering, per-queue small multiples, and Queue × Hour view can
    /// be exercised with more than the single <c>default</c> queue. These use hourly/daily crons so
    /// they show as their own rows without flooding the grid like the sub-hourly demo jobs.
    /// </summary>
    public static void SeedSecondaryQueues()
    {
        // "reports" queue — an hourly rollup and a daily export.
        RecurringJob.AddOrUpdate<SampleJobs>(
            "reports-hourly-rollup", "reports", x => x.SimpleJob(), "0 * * * *");
        RecurringJob.AddOrUpdate<SampleJobs>(
            "reports-nightly-export", "reports", x => x.SimpleJob(), "0 2 * * *");

        // "maintenance" queue — a daily cleanup.
        RecurringJob.AddOrUpdate<SampleJobs>(
            "maintenance-cleanup", "maintenance", x => x.SimpleJob(), "30 3 * * *");
    }
}
