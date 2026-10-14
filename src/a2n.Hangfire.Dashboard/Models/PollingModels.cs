namespace a2n.Hangfire.Dashboard;

/// <summary>
/// How the dashboard reads storage in the background: how many synchronous storage calls it runs
/// at once, and how its refresh loops slow down while storage is failing or slow (issue #64).
/// </summary>
/// <remarks>
/// Hangfire's storage APIs are synchronous, so every dashboard read holds a thread-pool thread
/// for as long as storage takes to answer, including the time spent waiting for a pooled
/// connection. These options bound that cost so a slow storage cannot starve the host's thread
/// pool or exhaust the connection pool the Hangfire server needs for its own heartbeat.
/// </remarks>
public class PollingOptions
{
    /// <summary>Default for <see cref="MaxConcurrentStorageCalls"/>.</summary>
    public const int DefaultMaxConcurrentStorageCalls = 8;

    /// <summary>
    /// Maximum number of the dashboard's synchronous storage calls that run at the same time,
    /// across every open tab. Further calls wait (asynchronously, holding no thread) for a free
    /// slot. A call keeps its slot until storage actually answers, even when the page that started
    /// it has stopped waiting, so the number of threads the dashboard can block stays at this
    /// limit. Values below 1 are treated as 1. Read when the dashboard first reads storage.
    /// Default: 8.
    /// </summary>
    public int MaxConcurrentStorageCalls { get; set; } = DefaultMaxConcurrentStorageCalls;

    /// <summary>
    /// Longest interval a refresh loop backs off to. When a refresh fails or takes longer than
    /// <see cref="SlowCallThreshold"/>, its interval doubles, up to this value, and returns to
    /// normal on the first refresh that succeeds in time. A loop whose normal interval is already
    /// longer is not slowed further. Default: 60 seconds.
    /// </summary>
    public TimeSpan MaxBackoffInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A refresh that takes at least this long counts as slow and backs its loop off like a failed
    /// one. Default: 5 seconds.
    /// </summary>
    public TimeSpan SlowCallThreshold { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often pages that follow running work refresh while open: Processing, Job Details and
    /// Queues. <see cref="TimeSpan.Zero"/> turns their automatic refresh off. Default: 3 seconds.
    /// </summary>
    public TimeSpan FastPageRefreshInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How often the job list pages (Enqueued, Fetched, Scheduled, Succeeded, Failed, Deleted,
    /// Awaiting), Retries and the maintenance banner refresh while open.
    /// <see cref="TimeSpan.Zero"/> turns their automatic refresh off. Default: 5 seconds.
    /// </summary>
    public TimeSpan PageRefreshInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often Recurring Jobs and the sidebar's tag and paused-queue counts refresh.
    /// <see cref="TimeSpan.Zero"/> turns their automatic refresh off. Default: 10 seconds.
    /// </summary>
    public TimeSpan SlowPageRefreshInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often a live job console (a job still processing) reads new lines.
    /// <see cref="TimeSpan.Zero"/> turns live updates off. Default: 2 seconds.
    /// </summary>
    public TimeSpan ConsoleRefreshInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a computed health report is reused by the <c>/healthz</c> endpoints, the ASP.NET
    /// Core health check and the Home page's health card before it is computed again.
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan HealthReportCacheTtl { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often the health card on the Home page refreshes. <see cref="TimeSpan.Zero"/> turns its
    /// automatic refresh off (the Refresh button still works). Default: 10 seconds.
    /// </summary>
    public TimeSpan HealthCardRefreshInterval { get; set; } = TimeSpan.FromSeconds(10);
}
