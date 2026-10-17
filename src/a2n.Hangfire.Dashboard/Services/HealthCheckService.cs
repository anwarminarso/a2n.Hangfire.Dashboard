using System.Diagnostics;
using System.Reflection;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.Logging;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Aggregates the health of the Hangfire dashboard runtime: storage availability, server liveness,
/// stuck processing jobs, queue depth, recent failure rate, and missed recurring schedules.
/// </summary>
/// <remarks>
/// Checks are evaluated lazily and independently. A single failing check (e.g., storage timeout)
/// does not prevent other checks from running. Each individual check produces a
/// <see cref="HealthCheckResult"/>; the overall <see cref="HealthReport.Status"/> is the worst
/// status of any contributing check.
/// </remarks>
public class HealthCheckService
{
    private static readonly string PackageVersion = ResolvePackageVersion();

    private static readonly string[] StorageDependentChecks =
        ["servers", "queue_depth", "stuck_processing", "failure_rate", "recurring_jobs"];

    private readonly HangfireMonitorService _monitor;
    private readonly DashboardUIOptions _options;
    private readonly ILogger<HealthCheckService> _logger;
    private readonly DashboardMetricsFeed _feed;
    private readonly DashboardStorageGate _gate;

    // Kept so assemblies compiled against 2.5.x find it.
    public HealthCheckService(
        HangfireMonitorService monitor,
        DashboardUIOptions options,
        ILogger<HealthCheckService> logger = null)
        : this(monitor, options, logger, feed: null, gate: null)
    {
    }

    /// <param name="monitor">Storage reads.</param>
    /// <param name="options">Thresholds.</param>
    /// <param name="logger">Optional.</param>
    /// <param name="feed">
    /// When given, the storage probe reuses the shared metrics snapshot instead of reading the
    /// statistics again, and at most one probe read is ever in flight.
    /// </param>
    /// <param name="gate">When given, the other checks' storage reads go through it.</param>
    public HealthCheckService(
        HangfireMonitorService monitor,
        DashboardUIOptions options,
        ILogger<HealthCheckService> logger,
        DashboardMetricsFeed feed,
        DashboardStorageGate gate)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _feed = feed;
        _gate = gate;
    }

    /// <summary>
    /// Runs only the storage probe. Suitable for liveness probes (K8s) where the only goal is
    /// "the dashboard process is alive and can talk to its storage".
    /// </summary>
    /// <remarks>Blocks the calling thread; prefer <see cref="CheckLivenessAsync"/>.</remarks>
    public HealthReport CheckLiveness() => CheckLivenessAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Runs liveness checks plus a server-presence check. Suitable for readiness probes — answers
    /// "is the dashboard ready to take traffic and is at least one Hangfire server running?".
    /// </summary>
    /// <remarks>Blocks the calling thread; prefer <see cref="CheckReadinessAsync"/>.</remarks>
    public HealthReport CheckReadiness() => CheckReadinessAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Runs all checks and returns a full diagnostic report. Use for monitoring / status pages /
    /// the dashboard hero card. More expensive than <see cref="CheckLiveness"/> — this iterates
    /// queues, processing jobs, and recurring jobs.
    /// </summary>
    /// <remarks>Blocks the calling thread; prefer <see cref="CheckFullAsync"/>.</remarks>
    public HealthReport CheckFull() => CheckFullAsync().GetAwaiter().GetResult();

    /// <summary>Asynchronous <see cref="CheckLiveness"/>.</summary>
    /// <param name="forceProbe">
    /// Read storage now rather than reuse a metrics snapshot younger than
    /// <see cref="DashboardUIOptions.StatsPollingInterval"/> (a manual refresh).
    /// </param>
    /// <param name="ct">Stops waiting; a storage call already started completes in the background.</param>
    public async Task<HealthReport> CheckLivenessAsync(bool forceProbe = false, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var report = NewReport();

        var probe = await ProbeStorageAsync(forceProbe, ct).ConfigureAwait(false);
        report.Checks["storage"] = probe.Result;
        report.Status = probe.Result.Status;
        report.DurationMs = sw.ElapsedMilliseconds;
        return report;
    }

    /// <summary>Asynchronous <see cref="CheckReadiness"/>.</summary>
    /// <inheritdoc cref="CheckLivenessAsync" path="/param"/>
    public async Task<HealthReport> CheckReadinessAsync(bool forceProbe = false, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var report = NewReport();

        var probe = await ProbeStorageAsync(forceProbe, ct).ConfigureAwait(false);
        report.Checks["storage"] = probe.Result;
        report.Checks["servers"] = probe.Reachable
            ? await ServersCheckAsync(probe, ct).ConfigureAwait(false)
            : Skipped();
        report.Status = AggregateStatus(report.Checks.Values);
        report.DurationMs = sw.ElapsedMilliseconds;
        return report;
    }

    /// <summary>Asynchronous <see cref="CheckFull"/>.</summary>
    /// <remarks>
    /// When the storage probe fails or times out, the other checks are not run (each is reported
    /// as skipped): with storage unreachable they could only fail, each after its own timeout.
    /// </remarks>
    /// <inheritdoc cref="CheckLivenessAsync" path="/param"/>
    public async Task<HealthReport> CheckFullAsync(bool forceProbe = false, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var report = NewReport();

        var probe = await ProbeStorageAsync(forceProbe, ct).ConfigureAwait(false);
        report.Checks["storage"] = probe.Result;

        if (!probe.Reachable)
        {
            foreach (var name in StorageDependentChecks)
                report.Checks[name] = Skipped();
        }
        else
        {
            // The probe also returns the statistics (and, from the feed, the servers), so the
            // checks below reuse them.
            var stats = probe.Statistics;
            report.Checks["servers"] = await ServersCheckAsync(probe, ct).ConfigureAwait(false);
            report.Checks["queue_depth"] = await RunCheckAsync("Queue depth", CheckQueueDepth, ct).ConfigureAwait(false);
            report.Checks["stuck_processing"] = await RunCheckAsync("Stuck-processing", () => CheckStuckProcessing(stats), ct).ConfigureAwait(false);
            report.Checks["failure_rate"] = await RunCheckAsync("Failure-rate", CheckFailureRate, ct).ConfigureAwait(false);
            report.Checks["recurring_jobs"] = await RunCheckAsync("Recurring", CheckRecurringJobs, ct).ConfigureAwait(false);
        }

        report.Status = AggregateStatus(report.Checks.Values);
        report.DurationMs = sw.ElapsedMilliseconds;
        return report;
    }

    private static HealthReport NewReport() => new()
    {
        Version = PackageVersion,
        Timestamp = DateTime.UtcNow,
    };

    /// <summary>
    /// The description of a probe that threw: the exception type only. The message can carry host
    /// names, addresses and connection details, so it goes to the server log (logged by the caller
    /// with the exception), never into a report.
    /// </summary>
    internal static string ProbeFailed(string probe, Exception ex)
        => $"{probe} probe failed: {ex.GetType().Name}. Details are in the server log.";

    private static HealthCheckResult Skipped() => new()
    {
        Status = HealthStatus.Unhealthy,
        Description = "Skipped: the storage probe failed.",
        Data = new() { ["skipped"] = true },
    };

    // ---- Running storage reads ----

    private sealed record StorageProbe(
        HealthCheckResult Result,
        bool Reachable,
        StatisticsDto Statistics = null,
        IReadOnlyList<ServerDto> Servers = null);

    private int StorageTimeoutMs => _options.HealthCheckThresholds.StorageProbeTimeoutMs;

    private CancellationTokenSource TimeoutSource(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (StorageTimeoutMs > 0) cts.CancelAfter(StorageTimeoutMs);
        return cts;
    }

    // Through the gate when there is one. Either way the caller stops waiting on cancellation; the
    // call itself can't be stopped (Hangfire's monitoring API is synchronous).
    private Task<T> RunAsync<T>(Func<T> call, CancellationToken ct)
        => _gate is not null ? _gate.RunAsync(call, ct) : Task.Run(call, CancellationToken.None).WaitAsync(ct);

    /// <summary>Runs a check's storage reads under the probe timeout; a timeout reports it Degraded.</summary>
    private async Task<HealthCheckResult> RunCheckAsync(string name, Func<HealthCheckResult> check, CancellationToken ct)
    {
        using var cts = TimeoutSource(ct);
        try
        {
            return await RunAsync(check, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new HealthCheckResult
            {
                Status = HealthStatus.Degraded,
                Description = $"{name} probe timed out after {StorageTimeoutMs}ms.",
                Data = new() { ["timedOut"] = true },
            };
        }
    }

    private Task<HealthCheckResult> ServersCheckAsync(StorageProbe probe, CancellationToken ct)
        => probe.Servers is not null
            ? Task.FromResult(EvaluateServers(probe.Servers))
            : RunCheckAsync("Server", CheckServers, ct);

    // ---- Individual checks ----

    /// <summary>
    /// Reads the statistics, from the shared metrics snapshot when there is a feed, under
    /// <see cref="HealthThresholds.StorageProbeTimeoutMs"/>.
    /// </summary>
    /// <remarks>
    /// The previous probe blocked a thread on <c>Task.Wait</c> and left the abandoned read running,
    /// so each probe after a timeout added another blocked thread (issue #64). The feed reads once
    /// for all callers and starts no second read while one is still blocked, so a stalled storage
    /// costs at most one thread here however often the probe runs.
    /// </remarks>
    private async Task<StorageProbe> ProbeStorageAsync(bool force, CancellationToken ct)
    {
        var result = new HealthCheckResult { Data = new() };
        var sw = Stopwatch.StartNew();
        using var cts = TimeoutSource(ct);

        StatisticsDto stats;
        IReadOnlyList<ServerDto> servers = null;
        TimeSpan storageTime;
        try
        {
            if (_feed is not null)
            {
                var snapshot = force
                    ? await _feed.RefreshAsync(cts.Token).ConfigureAwait(false)
                    : await _feed.GetMetricsAsync(cts.Token).ConfigureAwait(false);
                stats = snapshot.Statistics;
                servers = snapshot.Servers;
                // A reused snapshot answers at once, but says how long storage took to produce it.
                storageTime = snapshot.ReadDuration > sw.Elapsed ? snapshot.ReadDuration : sw.Elapsed;
            }
            else
            {
                stats = await RunAsync(_monitor.GetStatistics, cts.Token).ConfigureAwait(false);
                storageTime = sw.Elapsed;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result.Data["responseTimeMs"] = sw.ElapsedMilliseconds;
            result.Status = HealthStatus.Unhealthy;
            result.Description = $"Storage probe timed out after {StorageTimeoutMs}ms.";
            result.Data["timedOut"] = true;
            return new StorageProbe(result, Reachable: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Health check: storage probe failed");
            result.Status = HealthStatus.Unhealthy;
            result.Description = ProbeFailed("Storage", ex);
            result.Data["error"] = ex.GetType().FullName;
            return new StorageProbe(result, Reachable: false);
        }

        var elapsed = (long)storageTime.TotalMilliseconds;
        result.Data["responseTimeMs"] = elapsed;

        if (elapsed >= _options.HealthCheckThresholds.StorageResponseTimeCriticalMs)
        {
            result.Status = HealthStatus.Unhealthy;
            result.Description = $"Storage probe took {elapsed}ms (critical threshold {_options.HealthCheckThresholds.StorageResponseTimeCriticalMs}ms).";
        }
        else if (elapsed >= _options.HealthCheckThresholds.StorageResponseTimeWarnMs)
        {
            result.Status = HealthStatus.Degraded;
            result.Description = $"Storage probe took {elapsed}ms (warn threshold {_options.HealthCheckThresholds.StorageResponseTimeWarnMs}ms).";
        }
        else
        {
            result.Status = HealthStatus.Healthy;
            result.Description = "Storage reachable.";
        }

        return new StorageProbe(result, Reachable: true, stats, servers);
    }

    private HealthCheckResult CheckServers()
    {
        IReadOnlyList<ServerDto> servers;
        try
        {
            servers = _monitor.GetServers()?.ToArray() ?? [];
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Health check: server probe failed");
            return new HealthCheckResult
            {
                Status = HealthStatus.Degraded,
                Description = ProbeFailed("Server", ex),
                Data = new(),
            };
        }
        return EvaluateServers(servers);
    }

    private HealthCheckResult EvaluateServers(IReadOnlyList<ServerDto> servers)
    {
        var result = new HealthCheckResult { Data = new() };

        try
        {
            var tolerance = _options.HealthCheckThresholds.ServerHeartbeatTolerance;
            var threshold = DateTime.UtcNow - tolerance;

            var total = servers?.Count ?? 0;
            var alive = 0;
            var stale = 0;

            if (servers != null)
            {
                foreach (var s in servers)
                {
                    // Server is "alive" if Heartbeat is null (never reported, treat as alive)
                    // or if Heartbeat is recent within tolerance.
                    if (!s.Heartbeat.HasValue || s.Heartbeat.Value >= threshold)
                        alive++;
                    else
                        stale++;
                }
            }

            result.Data["total"] = total;
            result.Data["alive"] = alive;
            result.Data["stale"] = stale;
            result.Data["heartbeatToleranceSeconds"] = (int)tolerance.TotalSeconds;

            if (total == 0)
            {
                result.Status = HealthStatus.Unhealthy;
                result.Description = "No Hangfire servers registered.";
            }
            else if (alive == 0)
            {
                result.Status = HealthStatus.Unhealthy;
                result.Description = $"All {total} server(s) have stale heartbeats (>{(int)tolerance.TotalSeconds}s).";
            }
            else if (stale > 0)
            {
                result.Status = HealthStatus.Degraded;
                result.Description = $"{stale} of {total} server(s) have stale heartbeats.";
            }
            else
            {
                result.Status = HealthStatus.Healthy;
                result.Description = $"{alive} server(s) responding.";
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Health check: server probe failed");
            result.Status = HealthStatus.Degraded;
            result.Description = ProbeFailed("Server", ex);
        }

        return result;
    }

    private HealthCheckResult CheckQueueDepth()
    {
        var result = new HealthCheckResult { Data = new() };

        try
        {
            var queues = _monitor.GetQueues();
            var depths = new Dictionary<string, long>();
            long maxDepth = 0;
            string maxQueue = null;

            if (queues != null)
            {
                foreach (var q in queues)
                {
                    depths[q.Name] = q.Length;
                    if (q.Length > maxDepth)
                    {
                        maxDepth = q.Length;
                        maxQueue = q.Name;
                    }
                }
            }

            result.Data["queues"] = depths;
            result.Data["maxDepth"] = maxDepth;
            result.Data["maxDepthQueue"] = maxQueue;
            result.Data["warnThreshold"] = _options.HealthCheckThresholds.QueueDepthWarn;
            result.Data["criticalThreshold"] = _options.HealthCheckThresholds.QueueDepthCritical;

            if (maxDepth >= _options.HealthCheckThresholds.QueueDepthCritical)
            {
                result.Status = HealthStatus.Unhealthy;
                result.Description = $"Queue '{maxQueue}' has {maxDepth} jobs (critical threshold {_options.HealthCheckThresholds.QueueDepthCritical}).";
            }
            else if (maxDepth >= _options.HealthCheckThresholds.QueueDepthWarn)
            {
                result.Status = HealthStatus.Degraded;
                result.Description = $"Queue '{maxQueue}' has {maxDepth} jobs (warn threshold {_options.HealthCheckThresholds.QueueDepthWarn}).";
            }
            else
            {
                result.Status = HealthStatus.Healthy;
                result.Description = depths.Count == 0
                    ? "No queues."
                    : $"Highest queue depth: {maxDepth} ({maxQueue ?? "n/a"}).";
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Health check: queue depth probe failed");
            result.Status = HealthStatus.Degraded;
            result.Description = ProbeFailed("Queue depth", ex);
        }

        return result;
    }

    private HealthCheckResult CheckStuckProcessing(StatisticsDto stats)
    {
        var result = new HealthCheckResult { Data = new() };

        try
        {
            // Iterate the first page of processing jobs only — bound the cost of this probe.
            const int sampleSize = 200;
            var jobs = _monitor.GetProcessingJobs(0, sampleSize);
            var stuckThreshold = TimeSpan.FromMinutes(_options.HealthCheckThresholds.StuckProcessingMinutes);
            var now = DateTime.UtcNow;

            var stuckCount = 0;
            TimeSpan? oldestStuck = null;

            if (jobs != null)
            {
                foreach (var kv in jobs)
                {
                    var dto = kv.Value;
                    if (dto?.StartedAt is null || !dto.InProcessingState) continue;

                    var age = now - dto.StartedAt.Value;
                    if (age >= stuckThreshold)
                    {
                        stuckCount++;
                        if (!oldestStuck.HasValue || age > oldestStuck.Value)
                            oldestStuck = age;
                    }
                }
            }

            result.Data["stuckCount"] = stuckCount;
            result.Data["sampleSize"] = sampleSize;
            result.Data["totalProcessing"] = stats?.Processing ?? 0;
            result.Data["thresholdMinutes"] = _options.HealthCheckThresholds.StuckProcessingMinutes;
            if (oldestStuck.HasValue)
                result.Data["oldestStuckMinutes"] = (int)oldestStuck.Value.TotalMinutes;

            if (stuckCount >= _options.HealthCheckThresholds.StuckProcessingCritical)
            {
                result.Status = HealthStatus.Unhealthy;
                result.Description = $"Critical: {stuckCount} processing job(s) stuck >{_options.HealthCheckThresholds.StuckProcessingMinutes}m (sampled first {sampleSize}).";
            }
            else if (stuckCount > 0)
            {
                result.Status = HealthStatus.Degraded;
                result.Description = $"Warning: {stuckCount} processing job(s) stuck >{_options.HealthCheckThresholds.StuckProcessingMinutes}m (sampled first {sampleSize}).";
            }
            else
            {
                // Also flag total processing count as Degraded if it's huge — capacity warning.
                var processingTotal = stats?.Processing ?? 0;
                if (processingTotal >= _options.HealthCheckThresholds.ProcessingCountWarn)
                {
                    result.Status = HealthStatus.Degraded;
                    result.Description = $"{processingTotal} jobs processing concurrently (warn threshold {_options.HealthCheckThresholds.ProcessingCountWarn}).";
                }
                else
                {
                    result.Status = HealthStatus.Healthy;
                    result.Description = processingTotal == 0
                        ? "No jobs processing."
                        : $"{processingTotal} job(s) processing, none stuck.";
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Health check: stuck processing probe failed");
            result.Status = HealthStatus.Degraded;
            result.Description = ProbeFailed("Stuck-processing", ex);
        }

        return result;
    }

    private HealthCheckResult CheckFailureRate()
    {
        var result = new HealthCheckResult { Data = new() };

        try
        {
            // Hangfire stores hourly counters for the last 24 hours. We sum the two most recent
            // buckets (the partially-elapsed current hour plus the previous full hour) to form a
            // rolling ~1-2h window. Sampling a single bucket is unstable: at the start of a clock
            // hour a lone failure with no successes yields a misleading 100% rate, which — for a
            // readiness probe returning 503 — could drop a healthy pod out of rotation.
            var hourlySucceeded = _monitor.GetHourlySucceededJobs();
            var hourlyFailed = _monitor.GetHourlyFailedJobs();

            const int windowHours = 2;
            var windowSucceeded = hourlySucceeded?
                .OrderByDescending(kv => kv.Key)
                .Take(windowHours)
                .Sum(kv => kv.Value) ?? 0;
            var windowFailed = hourlyFailed?
                .OrderByDescending(kv => kv.Key)
                .Take(windowHours)
                .Sum(kv => kv.Value) ?? 0;

            var total = windowSucceeded + windowFailed;
            double percent = total == 0 ? 0 : (double)windowFailed * 100.0 / total;
            var minimumSample = _options.HealthCheckThresholds.FailureRateMinimumSample;

            result.Data["windowHours"] = windowHours;
            result.Data["windowSucceeded"] = windowSucceeded;
            result.Data["windowFailed"] = windowFailed;
            result.Data["windowPercent"] = Math.Round(percent, 2);
            result.Data["minimumSample"] = minimumSample;
            result.Data["warnThreshold"] = _options.HealthCheckThresholds.FailureRatePercent;
            result.Data["criticalThreshold"] = _options.HealthCheckThresholds.FailureRateCritical;

            if (total == 0)
            {
                result.Status = HealthStatus.Healthy;
                result.Description = $"No completed jobs in the last {windowHours}h.";
            }
            else if (total < minimumSample)
            {
                // Not enough data to draw a conclusion — stay Healthy to avoid flapping the probe.
                result.Status = HealthStatus.Healthy;
                result.Description = $"Failure rate {percent:F1}% ({windowFailed}/{total}) — below minimum sample of {minimumSample}, treated as Healthy.";
            }
            else if (percent >= _options.HealthCheckThresholds.FailureRateCritical)
            {
                result.Status = HealthStatus.Unhealthy;
                result.Description = $"Failure rate {percent:F1}% over last {windowHours}h (critical threshold {_options.HealthCheckThresholds.FailureRateCritical}%).";
            }
            else if (percent >= _options.HealthCheckThresholds.FailureRatePercent)
            {
                result.Status = HealthStatus.Degraded;
                result.Description = $"Failure rate {percent:F1}% over last {windowHours}h (warn threshold {_options.HealthCheckThresholds.FailureRatePercent}%).";
            }
            else
            {
                result.Status = HealthStatus.Healthy;
                result.Description = $"Failure rate {percent:F1}% over last {windowHours}h ({windowFailed}/{total}).";
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Health check: failure rate probe failed");
            result.Status = HealthStatus.Degraded;
            result.Description = ProbeFailed("Failure-rate", ex);
        }

        return result;
    }

    private HealthCheckResult CheckRecurringJobs()
    {
        var result = new HealthCheckResult { Data = new() };

        try
        {
            var recurring = _monitor.GetRecurringJobs();
            var tolerance = _options.HealthCheckThresholds.RecurringMissedTolerance;
            var now = DateTime.UtcNow;

            var total = recurring?.Count ?? 0;
            var missed = 0;
            var failing = 0;
            string firstMissedId = null;

            if (recurring != null)
            {
                foreach (var job in recurring)
                {
                    // "Missed" — NextExecution is in the past beyond the tolerance window.
                    if (job.NextExecution.HasValue && job.NextExecution.Value < now - tolerance)
                    {
                        missed++;
                        firstMissedId ??= job.Id;
                    }

                    // "Failing" — last invocation result was Failed/Deleted.
                    if (string.Equals(job.LastJobState, "Failed", StringComparison.OrdinalIgnoreCase))
                        failing++;
                }
            }

            result.Data["total"] = total;
            result.Data["missed"] = missed;
            result.Data["failing"] = failing;
            result.Data["toleranceSeconds"] = (int)tolerance.TotalSeconds;
            if (firstMissedId is not null)
                result.Data["firstMissedId"] = firstMissedId;

            if (missed > 0)
            {
                // Missed schedules → Degraded (servers may be slow or overloaded).
                result.Status = HealthStatus.Degraded;
                result.Description = $"{missed} of {total} recurring job(s) missed schedule by >{(int)tolerance.TotalMinutes}m.";
            }
            else if (failing > 0)
            {
                result.Status = HealthStatus.Degraded;
                result.Description = $"{failing} recurring job(s) last completed with Failed state.";
            }
            else
            {
                result.Status = HealthStatus.Healthy;
                result.Description = total == 0 ? "No recurring jobs." : $"{total} recurring job(s) on schedule.";
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Health check: recurring probe failed");
            result.Status = HealthStatus.Degraded;
            result.Description = ProbeFailed("Recurring", ex);
        }

        return result;
    }

    // ---- Helpers ----

    private static HealthStatus AggregateStatus(IEnumerable<HealthCheckResult> checks)
    {
        var worst = HealthStatus.Healthy;
        foreach (var c in checks)
        {
            if (c.Status > worst) worst = c.Status;
            if (worst == HealthStatus.Unhealthy) break;
        }
        return worst;
    }

    private static string ResolvePackageVersion()
    {
        try
        {
            var asm = typeof(HealthCheckService).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                // Strip git hash suffix "+abcdef" if present.
                var plus = info.IndexOf('+');
                return plus > 0 ? info[..plus] : info;
            }
            return asm.GetName().Version?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
