using System.Globalization;
using System.Reflection;
using a2n.Hangfire.Dashboard.Heatmap;
using Hangfire;
using Hangfire.Common;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace a2n.Hangfire.Dashboard.Rollup;

/// <summary>
/// Unified background collector that polls recent executions and maintains both the ad-hoc
/// demand rollup and the metrics rollup in a single pass.
/// </summary>
public sealed class ExecutionRollupCollector : IHostedService, IDisposable
{
    private const int PageSize = 200;
    private const int MaxJobsPerPoll = 2000;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);

    private readonly JobStorage _storage;
    private readonly MetricsRollupStore _store;
    private readonly ILogger<ExecutionRollupCollector> _logger;
    private CancellationTokenSource _stoppingCts;
    private Task _loopTask;

    public ExecutionRollupCollector(IServiceProvider serviceProvider)
    {
        _storage = serviceProvider.GetService<JobStorage>();
        _logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger<ExecutionRollupCollector>()
                  ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ExecutionRollupCollector>.Instance;
        _store = new MetricsRollupStore(
            serviceProvider.GetService<ILoggerFactory>()?.CreateLogger<MetricsRollupStore>());
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_storage == null)
        {
            _logger.LogDebug("ExecutionRollupCollector is inactive: no job storage is registered.");
            return Task.CompletedTask;
        }

        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = Task.Run(() => RunLoopAsync(_stoppingCts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_loopTask == null)
            return;

        try { _stoppingCts?.Cancel(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to signal cancellation."); }

        try
        {
            await Task.WhenAny(_loopTask, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _stoppingCts?.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(InitialDelay, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try { PollOnce(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { _logger.LogError(ex, "Rollup poll failed; will retry on the next interval."); }
        }
        while (await WaitForNextTickAsync(timer, ct).ConfigureAwait(false));
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }

    private void PollOnce(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        IMonitoringApi monitoringApi;
        try { monitoringApi = _storage.GetMonitoringApi(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to obtain the Hangfire monitoring API for rollup collection.");
            return;
        }

        using var connection = _storage.GetConnection();
        if (connection == null)
            return;

        var nowTicks = DateTime.UtcNow.Ticks;
        var (succeededWatermark, failedWatermark, hasState) = _store.ReadWatermarks(connection);

        if (!hasState)
        {
            _store.SeedWatermarks(connection, nowTicks);
            _logger.LogInformation("Metrics rollup initialized; collecting execution metrics from now forward.");
            return;
        }

        var accumulator = new RollupAccumulator();
        var succeeded = ProcessSucceeded(monitoringApi, connection, succeededWatermark, accumulator, ct);
        var failed = ProcessFailed(monitoringApi, connection, failedWatermark, accumulator, ct);

        WarnIfTruncated(succeeded.CapReached, "succeeded");
        WarnIfTruncated(failed.CapReached, "failed");

        ct.ThrowIfCancellationRequested();
        _store.Commit(connection, succeeded.Watermark, failed.Watermark, accumulator, Internal.RollupTime.WeekIndex(nowTicks));
    }

    /// <summary>
    /// Outcome of one scan pass. <see cref="CapReached"/> means the pass stopped at
    /// <see cref="MaxJobsPerPoll"/> while still seeing jobs newer than the watermark, so executions
    /// older than the scanned window were not aggregated: the watermark advances past them.
    /// </summary>
    private readonly record struct ScanResult(long Watermark, bool CapReached);

    private void WarnIfTruncated(bool capReached, string state)
    {
        if (!capReached)
            return;

        _logger.LogWarning(
            "Rollup collector hit its per-poll cap of {Cap} {State} jobs. More than {Cap} {State} jobs " +
            "completed within one {Interval}s poll, so executions beyond the scanned window are not " +
            "reflected in Analytics. Rollup metrics are a sample under this load; use the SQL Server or " +
            "PostgreSQL adapter when complete history is required.",
            MaxJobsPerPoll, state, MaxJobsPerPoll, state, (int)PollInterval.TotalSeconds);
    }

    private ScanResult ProcessSucceeded(
        IMonitoringApi api,
        IStorageConnection connection,
        long watermarkTicks,
        RollupAccumulator accumulator,
        CancellationToken ct)
    {
        var newWatermark = watermarkTicks;
        var offset = 0;
        var capReached = false;

        while (offset < MaxJobsPerPoll)
        {
            ct.ThrowIfCancellationRequested();

            JobList<SucceededJobDto> page;
            try { page = api.SucceededJobs(offset, PageSize); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read succeeded jobs for rollup collection.");
                break;
            }

            if (page == null || page.Count == 0)
                break;

            var sawNew = false;
            foreach (var entry in page)
            {
                var jobId = entry.Key;
                var dto = entry.Value;
                if (dto?.SucceededAt is not DateTime succeededAt)
                    continue;

                var ticks = Internal.RollupTime.AsUtcTicks(succeededAt);
                if (ticks <= watermarkTicks)
                    continue;

                sawNew = true;
                if (ticks > newWatermark)
                    newWatermark = ticks;

                // Prefer the Succeeded state's own measurements: PerformanceDuration is the pure
                // execution time and Latency the enqueued→processing wait, matching what the SQL
                // adapters read. TotalDuration is the sum of both, so it is only a fallback for
                // storages that do not persist the state data.
                var performanceDuration = ReadStateDataLong(dto.StateData, "PerformanceDuration")
                                          ?? dto.TotalDuration;
                var latency = ReadStateDataLong(dto.StateData, "Latency");

                var exec = BuildExecution(connection, jobId, dto.Job, succeededAt, succeeded: true, performanceDuration, latency);
                accumulator.Record(exec);
            }

            offset += page.Count;
            if (page.Count < PageSize || !sawNew)
                break;

            if (offset >= MaxJobsPerPoll)
            {
                capReached = true;
                break;
            }
        }

        return new ScanResult(newWatermark, capReached);
    }

    private ScanResult ProcessFailed(
        IMonitoringApi api,
        IStorageConnection connection,
        long watermarkTicks,
        RollupAccumulator accumulator,
        CancellationToken ct)
    {
        var newWatermark = watermarkTicks;
        var offset = 0;
        var capReached = false;

        while (offset < MaxJobsPerPoll)
        {
            ct.ThrowIfCancellationRequested();

            JobList<FailedJobDto> page;
            try { page = api.FailedJobs(offset, PageSize); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read failed jobs for rollup collection.");
                break;
            }

            if (page == null || page.Count == 0)
                break;

            var sawNew = false;
            foreach (var entry in page)
            {
                var jobId = entry.Key;
                var dto = entry.Value;
                if (dto?.FailedAt is not DateTime failedAt)
                    continue;

                var ticks = Internal.RollupTime.AsUtcTicks(failedAt);
                if (ticks <= watermarkTicks)
                    continue;

                sawNew = true;
                if (ticks > newWatermark)
                    newWatermark = ticks;

                var exec = BuildExecution(connection, jobId, dto.Job, failedAt, succeeded: false, totalDuration: null, latency: null);
                exec.ExceptionType = dto.ExceptionType;
                accumulator.Record(exec);
            }

            offset += page.Count;
            if (page.Count < PageSize || !sawNew)
                break;

            if (offset >= MaxJobsPerPoll)
            {
                capReached = true;
                break;
            }
        }

        return new ScanResult(newWatermark, capReached);
    }

    private ProcessedExecution BuildExecution(
        IStorageConnection connection,
        string jobId,
        Job job,
        DateTime executedAt,
        bool succeeded,
        long? totalDuration,
        long? latency)
    {
        var recurringJobId = SafeGetParameter(connection, jobId, "RecurringJobId");
        var retryRaw = SafeGetParameter(connection, jobId, "RetryCount");
        int.TryParse(retryRaw, out var retryCount);

        var jobType = ResolveJobType(job);
        var queue = ResolveQueue(job);
        var duration = totalDuration is long d && d > 0 ? (double)d : 0d;
        var latencyMs = latency is long l && l > 0 ? (double)l : 0d;

        return new ProcessedExecution
        {
            JobId = jobId,
            ExecutedAtUtc = Internal.RollupTime.AsUtc(executedAt),
            Succeeded = succeeded,
            JobType = jobType,
            Queue = queue,
            RecurringJobId = recurringJobId,
            DurationMs = duration,
            LatencyMs = latencyMs,
            RetryCount = retryCount,
            JobName = jobType
        };
    }

    private static string SafeGetParameter(IStorageConnection connection, string jobId, string name)
    {
        try { return connection.GetJobParameter(jobId, name); }
        catch { return null; }
    }

    /// <summary>
    /// Reads a millisecond value from a job state's serialized data, returning <c>null</c> when the
    /// field is absent or unparseable.
    /// </summary>
    private static long? ReadStateDataLong(IDictionary<string, string> stateData, string field)
    {
        if (stateData == null || !stateData.TryGetValue(field, out var raw))
            return null;

        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string ResolveJobType(Job job)
    {
        if (job?.Type == null || job.Method == null)
            return "Unknown";

        return $"{job.Type.Name}.{job.Method.Name}";
    }

    private static string ResolveQueue(Job job)
    {
        if (job == null)
            return ScheduleAggregator.DefaultQueue;

        try
        {
            if (!string.IsNullOrWhiteSpace(job.Queue))
                return job.Queue;

            var attr = job.Method?.GetCustomAttribute<QueueAttribute>(inherit: true)
                       ?? job.Method?.DeclaringType?.GetCustomAttribute<QueueAttribute>(inherit: true);

            if (attr != null && !string.IsNullOrWhiteSpace(attr.Queue) && !attr.Queue.Contains('{'))
                return attr.Queue;
        }
        catch { }

        return ScheduleAggregator.DefaultQueue;
    }
}
