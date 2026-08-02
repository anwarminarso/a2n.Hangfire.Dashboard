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

    /// <summary>
    /// Upper bound on executions <em>aggregated</em> per state per poll. Each one costs two
    /// <see cref="IStorageConnection.GetJobParameter"/> round-trips, so this is what keeps a poll's cost
    /// bounded. Entries an earlier pass already covered are stepped over without spending budget.
    /// </summary>
    private const int MaxJobsPerPoll = 2000;

    /// <summary>
    /// Upper bound on pages read per state per poll while a backlog is being drained. A resuming pass
    /// has to page over the range it already covered before it reaches the pending gap, which costs one
    /// read per <see cref="PageSize"/> entries and no job-parameter lookups, so the budget is far larger
    /// than <see cref="MaxJobsPerPoll"/> allows for.
    /// </summary>
    private const int MaxPagesWhileResuming = 1000;

    /// <summary>
    /// Upper bound on pages read per state per poll with no backlog open. Every entry above the
    /// watermark is aggregated, so the record budget is spent within this many pages.
    /// </summary>
    private const int MaxPagesPerPoll = (MaxJobsPerPoll / PageSize) + 2;

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

    internal void PollOnce(CancellationToken ct)
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
        var (succeededCheckpoint, failedCheckpoint, hasState) = _store.ReadCheckpoints(connection);

        if (!hasState)
        {
            _store.SeedWatermarks(connection, nowTicks);
            _logger.LogInformation("Metrics rollup initialized; collecting execution metrics from now forward.");
            return;
        }

        var accumulator = new RollupAccumulator();

        var succeeded = Scan(
            "succeeded",
            succeededCheckpoint,
            (from, count) => monitoringApi.SucceededJobs(from, count),
            dto => dto.SucceededAt,
            (jobId, dto, succeededAt) =>
            {
                // Prefer the Succeeded state's own measurements: PerformanceDuration is the pure
                // execution time and Latency the enqueued→processing wait, matching what the SQL
                // adapters read. TotalDuration is the sum of both, so it is only a fallback for
                // storages that do not persist the state data.
                var performanceDuration = ReadStateDataLong(dto.StateData, "PerformanceDuration")
                                          ?? dto.TotalDuration;
                var latency = ReadStateDataLong(dto.StateData, "Latency");

                accumulator.Record(BuildExecution(
                    connection, jobId, dto.Job, succeededAt, succeeded: true, performanceDuration, latency));
            },
            ct);

        var failed = Scan(
            "failed",
            failedCheckpoint,
            (from, count) => monitoringApi.FailedJobs(from, count),
            dto => dto.FailedAt,
            (jobId, dto, failedAt) =>
            {
                var exec = BuildExecution(
                    connection, jobId, dto.Job, failedAt, succeeded: false, totalDuration: null, latency: null);
                exec.ExceptionType = dto.ExceptionType;
                accumulator.Record(exec);
            },
            ct);

        ReportScan(succeeded, "succeeded");
        ReportScan(failed, "failed");

        ct.ThrowIfCancellationRequested();
        _store.Commit(connection, succeeded.Checkpoint, failed.Checkpoint, accumulator,
            Internal.RollupTime.WeekIndex(nowTicks));
    }

    /// <summary>
    /// Pages one state list newest-first and aggregates everything the checkpoint has not covered yet.
    /// </summary>
    /// <remarks>
    /// The pass stops once it reaches the watermark (the checkpoint then collapses to a single
    /// watermark), when the list runs out, or when its budget is spent. In the last case the checkpoint
    /// records the range that was covered so the following polls can drain the remainder from the top
    /// down, instead of advancing the watermark past executions no pass ever looked at.
    /// </remarks>
    private Internal.ScanResult Scan<TDto>(
        string stateName,
        Internal.ScanCheckpoint checkpoint,
        Func<int, int, JobList<TDto>> readPage,
        Func<TDto, DateTime?> completedAt,
        Action<string, TDto, DateTime> record,
        CancellationToken ct)
        where TDto : class
    {
        var window = new Internal.ScanWindow(checkpoint, MaxJobsPerPoll);
        var pageBudget = checkpoint.HasGap ? MaxPagesWhileResuming : MaxPagesPerPoll;
        var offset = 0;
        var pagesRead = 0;
        var drained = false;
        var stop = false;

        while (!stop && pagesRead < pageBudget)
        {
            ct.ThrowIfCancellationRequested();

            JobList<TDto> page;
            try { page = readPage(offset, PageSize); }
            catch (Exception ex)
            {
                // Not drained: the checkpoint keeps whatever this pass covered and nothing is written off.
                _logger.LogError(ex, "Failed to read {State} jobs for rollup collection.", stateName);
                break;
            }

            if (page == null || page.Count == 0)
            {
                drained = true;
                break;
            }

            pagesRead++;

            foreach (var entry in page)
            {
                if (entry.Value == null || completedAt(entry.Value) is not DateTime executedAt)
                    continue;

                var action = window.Classify(Internal.RollupTime.AsUtcTicks(executedAt));
                if (action == Internal.ScanAction.Skip)
                    continue;

                if (action != Internal.ScanAction.Record)
                {
                    drained = action == Internal.ScanAction.StopDrained;
                    stop = true;
                    break;
                }

                record(entry.Key, entry.Value, executedAt);
                window.OnRecorded(Internal.RollupTime.AsUtcTicks(executedAt));
            }

            if (stop)
                break;

            offset += page.Count;
            if (page.Count < PageSize)
            {
                drained = true;
                break;
            }
        }

        return window.Complete(drained);
    }

    private void ReportScan(Internal.ScanResult result, string state)
    {
        if (result.DataDropped)
        {
            _logger.LogWarning(
                "Rollup collector cannot keep up with {State} job volume: more than {Cap} {State} jobs " +
                "completed while an earlier backlog was still being drained, so the executions between " +
                "the two scanned ranges are missing from Analytics. Sustained load above {Cap} {State} " +
                "jobs per {Interval}s poll exceeds what this collector can aggregate; use the SQL Server " +
                "or PostgreSQL adapter when complete history is required.",
                state, MaxJobsPerPoll, state, MaxJobsPerPoll, state, (int)PollInterval.TotalSeconds);
            return;
        }

        if (result.Checkpoint.HasGap)
        {
            _logger.LogInformation(
                "Rollup collector aggregated {Count} {State} jobs and reached its per-poll cap; a further " +
                "{Pending} of {State} executions is still pending and will be aggregated by the following " +
                "polls.",
                result.Recorded, state, result.Checkpoint.PendingSpan, state);
        }
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
