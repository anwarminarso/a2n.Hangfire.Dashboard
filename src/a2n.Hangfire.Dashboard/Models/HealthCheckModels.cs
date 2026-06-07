using System.Text.Json.Serialization;

namespace a2n.Hangfire.Dashboard;

/// <summary>
/// Authorization mode for the dashboard health check endpoint.
/// </summary>
public enum HealthCheckAuthorization
{
    /// <summary>
    /// Allow any caller. Sensible default for Kubernetes liveness/readiness probes which
    /// originate from the kubelet inside the cluster network without auth headers.
    /// </summary>
    AllowAnonymous = 0,

    /// <summary>
    /// Allow only requests originating from a loopback address. Use behind a reverse proxy where
    /// you intend the probe to come from the local node only.
    /// </summary>
    LocalOnly = 1,

    /// <summary>
    /// Apply the same authorization filters as the rest of the dashboard (the
    /// <see cref="DashboardUIOptions.Authorization"/> chain). Choose this when health responses
    /// should be visible only to authenticated dashboard users.
    /// </summary>
    RequireDashboardAuth = 2,
}

/// <summary>
/// Overall health status of the Hangfire dashboard. Values are ordered by severity ascending.
/// </summary>
public enum HealthStatus
{
    /// <summary>All checks passed.</summary>
    Healthy = 0,

    /// <summary>One or more checks reported a non-critical issue (e.g., high queue depth).</summary>
    Degraded = 1,

    /// <summary>One or more checks reported a critical issue (e.g., no servers, storage unreachable).</summary>
    Unhealthy = 2,
}

/// <summary>
/// Configurable thresholds for individual health checks. Values that are exceeded promote the
/// overall status to <see cref="HealthStatus.Degraded"/> or <see cref="HealthStatus.Unhealthy"/>.
/// </summary>
public class HealthThresholds
{
    /// <summary>
    /// Maximum number of jobs allowed in the processing state before the status becomes Degraded.
    /// Default: 100. The check uses the global processing count from <c>StatisticsDto</c>.
    /// </summary>
    public long ProcessingCountWarn { get; set; } = 100;

    /// <summary>
    /// Maximum age of a single processing job (in minutes). Jobs running longer than this are
    /// considered "stuck". Reaching this threshold promotes the status to Degraded.
    /// Default: 30 minutes.
    /// </summary>
    public int StuckProcessingMinutes { get; set; } = 30;

    /// <summary>
    /// Number of stuck processing jobs that promotes the overall status to Unhealthy.
    /// Below this number the check reports Degraded. Default: 5.
    /// </summary>
    public int StuckProcessingCritical { get; set; } = 5;

    /// <summary>
    /// Queue depth (per queue) that promotes the status to Degraded. Default: 1000.
    /// </summary>
    public long QueueDepthWarn { get; set; } = 1000;

    /// <summary>
    /// Queue depth (per queue) that promotes the status to Unhealthy. Default: 10000.
    /// </summary>
    public long QueueDepthCritical { get; set; } = 10000;

    /// <summary>
    /// Failure rate (percentage of failed jobs over total) in the last hour that promotes the
    /// status to Degraded. Default: 10.0 (%).
    /// </summary>
    public double FailureRatePercent { get; set; } = 10.0;

    /// <summary>
    /// Failure rate (percentage) that promotes the status to Unhealthy. Default: 25.0 (%).
    /// </summary>
    public double FailureRateCritical { get; set; } = 25.0;

    /// <summary>
    /// Minimum number of completed jobs (succeeded + failed) in the sampled window required before
    /// the failure-rate check produces a non-Healthy status. Guards against false positives where a
    /// single failure at the start of an hour yields a misleading 100% rate. Default: 20.
    /// </summary>
    public long FailureRateMinimumSample { get; set; } = 20;

    /// <summary>
    /// Tolerance window for recurring jobs that have missed their <see cref="Hangfire.Storage.RecurringJobDto.NextExecution"/>.
    /// A recurring job is "missed" when <c>NextExecution</c> is older than now minus this tolerance.
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan RecurringMissedTolerance { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum age of a server heartbeat before the server is considered offline. Default: 60 seconds.
    /// </summary>
    public TimeSpan ServerHeartbeatTolerance { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Maximum allowed milliseconds for the storage health probe (a single <c>GetStatistics</c> call)
    /// before the storage check is considered Degraded. Default: 1000ms.
    /// </summary>
    public int StorageResponseTimeWarnMs { get; set; } = 1000;

    /// <summary>
    /// Storage response time (ms) that marks the storage check Unhealthy. Default: 5000ms.
    /// </summary>
    public int StorageResponseTimeCriticalMs { get; set; } = 5000;

    /// <summary>
    /// Hard timeout (ms) for the storage probe. If the underlying <c>GetStatistics</c> call has not
    /// returned within this window, the storage check reports Unhealthy without waiting further.
    /// This bounds how long a hung storage backend can block health requests (and, by extension,
    /// the shared report cache). Default: 10000ms. Must be greater than
    /// <see cref="StorageResponseTimeCriticalMs"/> to be meaningful.
    /// </summary>
    public int StorageProbeTimeoutMs { get; set; } = 10000;
}

/// <summary>
/// Result of an individual health check.
/// </summary>
public class HealthCheckResult
{
    /// <summary>The status of this individual check.</summary>
    [JsonPropertyName("status")]
    public HealthStatus Status { get; set; }

    /// <summary>A short, human-readable description of the check outcome.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; }

    /// <summary>
    /// Optional structured data for the check (counts, thresholds, queue names, etc.).
    /// Serialized as part of the JSON response so monitoring tools can scrape values.
    /// </summary>
    [JsonPropertyName("data")]
    public Dictionary<string, object> Data { get; set; }
}

/// <summary>
/// Aggregated health report returned by the dashboard health endpoint.
/// </summary>
public class HealthReport
{
    /// <summary>Aggregated status (worst of all individual checks).</summary>
    [JsonPropertyName("status")]
    public HealthStatus Status { get; set; }

    /// <summary>Dashboard package version.</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; }

    /// <summary>UTC timestamp of when the report was generated.</summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    /// <summary>Total time taken to generate the report (in milliseconds).</summary>
    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    /// <summary>Individual checks keyed by name (e.g., "storage", "servers", "queue_depth").</summary>
    [JsonPropertyName("checks")]
    public Dictionary<string, HealthCheckResult> Checks { get; set; } = new();
}
