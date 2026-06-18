using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using a2n.Hangfire.Dashboard.Heatmap;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using Hangfire;
using Hangfire.Common;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Background service that incrementally maintains the ad-hoc <c>Demand_Rollup</c> — a persisted
/// aggregate of on-demand (non-recurring) execution counts and durations per
/// <c>queue × day-of-week × hour</c> — used by <see cref="DemandProfileProvider"/> to build the
/// heatmap's <c>Demand_Profile</c> (task 15.2).
/// </summary>
/// <remarks>
/// <para><b>Why a rollup.</b> Hangfire expires per-job records quickly (typically 24 hours), so a
/// profile read directly from job history would be untrustworthy over a multi-week lookback. This
/// service therefore polls recent succeeded/failed executions, classifies each as Cron or Ad-hoc via
/// <see cref="ExecutionClassifier"/> (only Ad-hoc executions — those with no <c>RecurringJobId</c> —
/// feed the rollup), and folds them into a durable aggregate that is maintained independently of
/// Hangfire's per-job expiration (Req 17.1, 17.2).</para>
///
/// <para><b>Storage-agnostic primitives, no schema change.</b> The rollup is stored entirely in
/// Hangfire storage primitives via the storage-agnostic connection/transaction API
/// (<see cref="IStorageConnection"/> / <see cref="IWriteOnlyTransaction"/>), so it works on any
/// Hangfire storage without a database migration (Req 17.3). The key scheme is:</para>
/// <list type="bullet">
///   <item><description><c>heatmap:demand:b:{weekIndex}:{queue}</c> — a hash whose fields are
///   <c>{dayOfWeek}:{hour}</c> (e.g. <c>3:14</c>) mapping to a packed <c>count|sumDurationMs</c>
///   sample. One hash per (queue, week) so an entire week can be dropped wholesale during
///   retention trimming.</description></item>
///   <item><description><c>heatmap:demand:queues</c> — a set of every queue ever observed, so the
///   trimmer and profile reader can enumerate queues.</description></item>
///   <item><description><c>heatmap:demand:weeks</c> — a set of every retained week index, so the
///   trimmer and profile reader can enumerate weeks and report the available span.</description></item>
///   <item><description><c>heatmap:demand:state</c> — a hash holding the per-source processing
///   watermarks (<c>succeededWatermarkTicks</c>, <c>failedWatermarkTicks</c>) so executions are
///   never double-counted across polls or process restarts.</description></item>
/// </list>
///
/// <para><b>Bounded retention.</b> Each poll trims any week older than
/// <see cref="RetentionWeeks"/> (which covers the maximum 8-week lookback plus the current partial
/// week) so the rollup never grows without limit (Req 17.5).</para>
///
/// <para><b>Forward-only.</b> On a cold start (no prior watermark) the service seeds the watermarks
/// to "now" rather than back-filling the (already short-lived) job history; the rollup's retained
/// span grows forward from first run, and the profile reader reports the actual available span
/// (Req 16.8, 17.4).</para>
///
/// <para><b>Registration.</b> This service is registered as an <see cref="IHostedService"/> only
/// when an <see cref="IStorageMetricsProvider"/> is present (wired in DI by task 15.4), mirroring the
/// Analytics graceful-degradation pattern — the ad-hoc/combined demand features light up only on
/// storages that support them. As a defensive measure it also no-ops at runtime when no metrics
/// provider or job storage is resolvable.</para>
///
/// <para><b>Robustness.</b> The poll loop never throws out of the background task: every storage
/// interaction is wrapped so a transient failure is logged and retried on the next tick, and the
/// service never crashes the host. It follows the existing <see cref="AnalyticsService"/> /
/// <see cref="HeatmapService"/> convention of taking an <see cref="IServiceProvider"/> and resolving
/// dependencies optionally.</para>
///
/// <para>Validates Requirements 17.1, 17.2, 17.3, and 17.5.</para>
/// </remarks>
public class DemandRollupService : IHostedService, IDisposable
{
    // ─── Storage key scheme (Req 17.3) ──────────────────────────────────────────
    private const string KeyPrefix = "heatmap:demand:";
    private const string QueuesSetKey = KeyPrefix + "queues";
    private const string WeeksSetKey = KeyPrefix + "weeks";
    private const string StateHashKey = KeyPrefix + "state";
    private const string SucceededWatermarkField = "succeededWatermarkTicks";
    private const string FailedWatermarkField = "failedWatermarkTicks";

    /// <summary>
    /// Number of weeks of rollup data to retain (Req 17.5). Covers the maximum 8-week
    /// <c>Lookback_Window</c> plus the current partial week.
    /// </summary>
    private const int RetentionWeeks = 9;

    /// <summary>How often the rollup is refreshed from recent executions.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    /// <summary>Delay before the first poll so the host can finish starting up.</summary>
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);

    /// <summary>Page size used when reading recent executions from the monitoring API.</summary>
    private const int PageSize = 200;

    /// <summary>Upper bound on executions examined per source per poll, so a poll is bounded.</summary>
    private const int MaxJobsPerPoll = 2000;

    private readonly IStorageMetricsProvider _metricsProvider;
    private readonly JobStorage _storage;
    private readonly ILogger<DemandRollupService> _logger;

    private CancellationTokenSource _stoppingCts;
    private Task _loopTask;

    /// <summary>
    /// Indicates whether the rollup will actually run (a metrics provider and job storage are both
    /// available). When false the service is an inert no-op (graceful degradation).
    /// </summary>
    public bool IsAvailable => _metricsProvider != null && _storage != null;

    public DemandRollupService(IServiceProvider serviceProvider)
    {
        if (serviceProvider is null)
        {
            throw new ArgumentNullException(nameof(serviceProvider));
        }

        // Resolve optionally — null when no metrics provider is registered (graceful degradation).
        _metricsProvider = serviceProvider.GetService<IStorageMetricsProvider>();
        _storage = serviceProvider.GetService<JobStorage>();
        _logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger<DemandRollupService>()
                  ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DemandRollupService>.Instance;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Defensive: only run when the ad-hoc demand features are actually supported (Req 16.7/16.9
        // gate registration to a metrics provider; this mirrors that at runtime).
        if (!IsAvailable)
        {
            _logger.LogDebug(
                "DemandRollupService is inactive: no metrics provider or job storage is registered.");
            return Task.CompletedTask;
        }

        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Run the loop detached from the StartAsync caller so host startup is not blocked.
        _loopTask = Task.Run(() => RunLoopAsync(_stoppingCts.Token));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_loopTask == null)
        {
            return;
        }

        try
        {
            _stoppingCts?.Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to signal cancellation while stopping the demand rollup service.");
        }

        try
        {
            // Wait for the loop to unwind, but never past the host's shutdown token.
            await Task.WhenAny(_loopTask, Task.Delay(Timeout.Infinite, cancellationToken))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown token fired — stop waiting; the loop observes its own cancellation.
        }
    }

    /// <summary>
    /// The background poll loop. Performs an initial delayed poll, then polls on a fixed interval
    /// until cancellation. Each poll is fully guarded so a failure never escapes the loop.
    /// </summary>
    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(InitialDelay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                PollOnce(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Never let a transient storage error crash the host; retry on the next tick.
                _logger.LogError(ex, "Demand rollup poll failed; will retry on the next interval.");
            }
        }
        while (await WaitForNextTickAsync(timer, ct).ConfigureAwait(false));
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Performs a single rollup pass: reads recent ad-hoc executions since the stored watermarks,
    /// folds them into the per-(queue, week) hashes, advances the watermarks, and trims weeks beyond
    /// the bounded retention — all committed in one storage-agnostic write transaction.
    /// </summary>
    private void PollOnce(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_storage == null)
        {
            return;
        }

        IMonitoringApi monitoringApi;
        try
        {
            monitoringApi = _storage.GetMonitoringApi();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to obtain the Hangfire monitoring API for the demand rollup.");
            return;
        }

        using var connection = _storage.GetConnection();
        if (connection == null)
        {
            return;
        }

        var nowTicks = DateTime.UtcNow.Ticks;
        var (succeededWatermark, failedWatermark, hasState) = ReadWatermarks(connection);

        // Cold start: seed forward-only so we don't back-fill the short-lived job history (Req 17.2).
        if (!hasState)
        {
            CommitState(connection, nowTicks, nowTicks, accumulator: null, currentWeek: WeekIndex(nowTicks));
            _logger.LogInformation("Demand rollup initialized; collecting ad-hoc demand from now forward.");
            return;
        }

        var accumulator = new Dictionary<RollupBucket, Sample>();

        var newSucceededWatermark = ProcessSucceeded(monitoringApi, connection, succeededWatermark, accumulator, ct);
        var newFailedWatermark = ProcessFailed(monitoringApi, connection, failedWatermark, accumulator, ct);

        ct.ThrowIfCancellationRequested();

        CommitState(connection, newSucceededWatermark, newFailedWatermark, accumulator, WeekIndex(nowTicks));
    }

    /// <summary>
    /// Reads recent succeeded executions newer than <paramref name="watermarkTicks"/>, accumulating
    /// the ad-hoc ones (Req 17.1). Returns the advanced watermark (the newest execution observed).
    /// </summary>
    private long ProcessSucceeded(
        IMonitoringApi api,
        IStorageConnection connection,
        long watermarkTicks,
        Dictionary<RollupBucket, Sample> accumulator,
        CancellationToken ct)
    {
        var newWatermark = watermarkTicks;
        var offset = 0;

        while (offset < MaxJobsPerPoll)
        {
            ct.ThrowIfCancellationRequested();

            JobList<SucceededJobDto> page;
            try
            {
                page = api.SucceededJobs(offset, PageSize);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read succeeded jobs for the demand rollup.");
                break;
            }

            if (page == null || page.Count == 0)
            {
                break;
            }

            var sawNew = false;
            foreach (var entry in page)
            {
                var jobId = entry.Key;
                var dto = entry.Value;
                if (dto?.SucceededAt is not DateTime succeededAt)
                {
                    continue;
                }

                var ticks = AsUtcTicks(succeededAt);
                if (ticks <= watermarkTicks)
                {
                    continue;
                }

                sawNew = true;
                if (ticks > newWatermark)
                {
                    newWatermark = ticks;
                }

                if (!IsAdHoc(connection, jobId))
                {
                    continue;
                }

                var durationMs = dto.TotalDuration is long total && total > 0 ? total : 0d;
                Accumulate(accumulator, succeededAt, ResolveQueue(dto.Job), durationMs);
            }

            offset += page.Count;
            // Lists are newest-first; once a whole page predates the watermark we are done.
            if (page.Count < PageSize || !sawNew)
            {
                break;
            }
        }

        return newWatermark;
    }

    /// <summary>
    /// Reads recent failed executions newer than <paramref name="watermarkTicks"/>, accumulating the
    /// ad-hoc ones with a zero duration sample (failures still represent demand). Returns the
    /// advanced watermark.
    /// </summary>
    private long ProcessFailed(
        IMonitoringApi api,
        IStorageConnection connection,
        long watermarkTicks,
        Dictionary<RollupBucket, Sample> accumulator,
        CancellationToken ct)
    {
        var newWatermark = watermarkTicks;
        var offset = 0;

        while (offset < MaxJobsPerPoll)
        {
            ct.ThrowIfCancellationRequested();

            JobList<FailedJobDto> page;
            try
            {
                page = api.FailedJobs(offset, PageSize);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read failed jobs for the demand rollup.");
                break;
            }

            if (page == null || page.Count == 0)
            {
                break;
            }

            var sawNew = false;
            foreach (var entry in page)
            {
                var jobId = entry.Key;
                var dto = entry.Value;
                if (dto?.FailedAt is not DateTime failedAt)
                {
                    continue;
                }

                var ticks = AsUtcTicks(failedAt);
                if (ticks <= watermarkTicks)
                {
                    continue;
                }

                sawNew = true;
                if (ticks > newWatermark)
                {
                    newWatermark = ticks;
                }

                if (!IsAdHoc(connection, jobId))
                {
                    continue;
                }

                Accumulate(accumulator, failedAt, ResolveQueue(dto.Job), durationMs: 0d);
            }

            offset += page.Count;
            if (page.Count < PageSize || !sawNew)
            {
                break;
            }
        }

        return newWatermark;
    }

    /// <summary>
    /// Classifies an execution as ad-hoc by the absence of a <c>RecurringJobId</c> job parameter
    /// (Req 17.1). A read failure conservatively treats the execution as Cron so it is excluded.
    /// </summary>
    private bool IsAdHoc(IStorageConnection connection, string jobId)
    {
        if (string.IsNullOrEmpty(jobId))
        {
            return false;
        }

        string recurringJobId = null;
        try
        {
            recurringJobId = connection.GetJobParameter(jobId, "RecurringJobId");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read RecurringJobId for job {JobId}; treating as non-ad-hoc.", jobId);
            return false;
        }

        return ExecutionClassifier.Classify(recurringJobId) == ExecutionClass.AdHoc;
    }

    /// <summary>
    /// Folds one execution into the in-memory accumulator keyed by its (week, queue, day-of-week,
    /// hour) bucket, derived from the execution's UTC instant.
    /// </summary>
    private static void Accumulate(
        Dictionary<RollupBucket, Sample> accumulator,
        DateTime executionUtc,
        string queue,
        double durationMs)
    {
        var utc = AsUtc(executionUtc);
        var bucket = new RollupBucket(
            WeekIndex(utc.Ticks),
            queue,
            (int)utc.DayOfWeek,
            utc.Hour);

        if (accumulator.TryGetValue(bucket, out var existing))
        {
            accumulator[bucket] = new Sample(existing.Count + 1, existing.SumDurationMs + durationMs);
        }
        else
        {
            accumulator[bucket] = new Sample(1, durationMs);
        }
    }

    /// <summary>
    /// Persists the accumulated samples, advanced watermarks, queue/week sets, and retention trim in
    /// a single storage-agnostic write transaction (Req 17.3, 17.5). When
    /// <paramref name="accumulator"/> is null only the watermarks and trim are written (cold start).
    /// </summary>
    private void CommitState(
        IStorageConnection connection,
        long succeededWatermark,
        long failedWatermark,
        Dictionary<RollupBucket, Sample> accumulator,
        long currentWeek)
    {
        // Merge each touched (queue, week) hash: read existing fields, add the new samples, then
        // write the combined values back. Reads happen on the connection before the transaction.
        var hashUpdates = new Dictionary<string, List<KeyValuePair<string, string>>>();
        var touchedQueues = new HashSet<string>(StringComparer.Ordinal);
        var touchedWeeks = new HashSet<long>();

        if (accumulator != null && accumulator.Count > 0)
        {
            // Group accumulated samples by their (week, queue) hash key.
            var byHash = new Dictionary<string, Dictionary<string, Sample>>(StringComparer.Ordinal);
            foreach (var kv in accumulator)
            {
                var bucket = kv.Key;
                var hashKey = BucketHashKey(bucket.Week, bucket.Queue);
                if (!byHash.TryGetValue(hashKey, out var fields))
                {
                    fields = new Dictionary<string, Sample>(StringComparer.Ordinal);
                    byHash[hashKey] = fields;
                }

                fields[FieldName(bucket.DayOfWeek, bucket.Hour)] = kv.Value;
                touchedQueues.Add(bucket.Queue);
                touchedWeeks.Add(bucket.Week);
            }

            foreach (var hash in byHash)
            {
                var existing = SafeReadHash(connection, hash.Key);
                var merged = new List<KeyValuePair<string, string>>(hash.Value.Count);
                foreach (var field in hash.Value)
                {
                    var prior = existing != null && existing.TryGetValue(field.Key, out var raw)
                        ? ParseSample(raw)
                        : default;
                    var combined = new Sample(prior.Count + field.Value.Count, prior.SumDurationMs + field.Value.SumDurationMs);
                    merged.Add(new KeyValuePair<string, string>(field.Key, PackSample(combined)));
                }

                hashUpdates[hash.Key] = merged;
            }
        }

        // Determine which weeks to trim (older than the bounded retention window, Req 17.5).
        var minKeepWeek = currentWeek - RetentionWeeks + 1;
        var knownWeeks = SafeReadSet(connection, WeeksSetKey);
        var knownQueues = SafeReadSet(connection, QueuesSetKey);

        var weeksToTrim = new List<long>();
        foreach (var raw in knownWeeks)
        {
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) && w < minKeepWeek)
            {
                weeksToTrim.Add(w);
            }
        }

        using var transaction = connection.CreateWriteTransaction();

        foreach (var update in hashUpdates)
        {
            transaction.SetRangeInHash(update.Key, update.Value);
        }

        foreach (var queue in touchedQueues)
        {
            transaction.AddToSet(QueuesSetKey, queue);
        }

        foreach (var week in touchedWeeks)
        {
            transaction.AddToSet(WeeksSetKey, week.ToString(CultureInfo.InvariantCulture));
        }

        // Trim every (queue, week) hash for expired weeks, then drop the week from the index set.
        foreach (var week in weeksToTrim)
        {
            foreach (var queue in knownQueues)
            {
                transaction.RemoveHash(BucketHashKey(week, queue));
            }

            transaction.RemoveFromSet(WeeksSetKey, week.ToString(CultureInfo.InvariantCulture));
        }

        transaction.SetRangeInHash(StateHashKey, new[]
        {
            new KeyValuePair<string, string>(SucceededWatermarkField, succeededWatermark.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>(FailedWatermarkField, failedWatermark.ToString(CultureInfo.InvariantCulture)),
        });

        transaction.Commit();
    }

    /// <summary>Reads the processing watermarks; <c>hasState=false</c> indicates a cold start.</summary>
    private (long Succeeded, long Failed, bool HasState) ReadWatermarks(IStorageConnection connection)
    {
        var state = SafeReadHash(connection, StateHashKey);
        if (state == null || state.Count == 0)
        {
            return (0, 0, false);
        }

        var succeeded = ParseTicks(state, SucceededWatermarkField);
        var failed = ParseTicks(state, FailedWatermarkField);
        return (succeeded, failed, true);
    }

    private static long ParseTicks(IReadOnlyDictionary<string, string> state, string field)
        => state.TryGetValue(field, out var raw)
           && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            ? ticks
            : 0L;

    private Dictionary<string, string> SafeReadHash(IStorageConnection connection, string key)
    {
        try
        {
            return connection.GetAllEntriesFromHash(key);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read demand rollup hash {Key}.", key);
            return null;
        }
    }

    private HashSet<string> SafeReadSet(IStorageConnection connection, string key)
    {
        try
        {
            return connection.GetAllItemsFromSet(key) ?? new HashSet<string>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read demand rollup set {Key}.", key);
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Resolves an execution's queue from its <see cref="Job"/>, falling back to
    /// <see cref="ScheduleAggregator.DefaultQueue"/> when it cannot be determined (mirrors the queue
    /// precedence used by <see cref="HeatmapService"/>).
    /// </summary>
    private static string ResolveQueue(Job job)
    {
        if (job == null)
        {
            return ScheduleAggregator.DefaultQueue;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(job.Queue))
            {
                return job.Queue;
            }

            var attr = job.Method?.GetCustomAttribute<QueueAttribute>(inherit: true)
                       ?? job.Method?.DeclaringType?.GetCustomAttribute<QueueAttribute>(inherit: true);

            if (attr != null && !string.IsNullOrWhiteSpace(attr.Queue) && !attr.Queue.Contains('{'))
            {
                return attr.Queue;
            }
        }
        catch
        {
            // Reflection / property access failure → fall through to the default queue.
        }

        return ScheduleAggregator.DefaultQueue;
    }

    // ─── Key & sample helpers ───────────────────────────────────────────────────

    /// <summary>Builds the per-(queue, week) hash key (Req 17.3).</summary>
    private static string BucketHashKey(long week, string queue)
        => $"{KeyPrefix}b:{week.ToString(CultureInfo.InvariantCulture)}:{queue}";

    /// <summary>Builds the <c>{dayOfWeek}:{hour}</c> hash field name.</summary>
    private static string FieldName(int dayOfWeek, int hour)
        => $"{dayOfWeek.ToString(CultureInfo.InvariantCulture)}:{hour.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Packs a sample as <c>count|sumDurationMs</c>.</summary>
    private static string PackSample(Sample sample)
        => $"{sample.Count.ToString(CultureInfo.InvariantCulture)}|{sample.SumDurationMs.ToString("R", CultureInfo.InvariantCulture)}";

    /// <summary>Parses a packed <c>count|sumDurationMs</c> sample; tolerant of malformed values.</summary>
    private static Sample ParseSample(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return default;
        }

        var sep = raw.IndexOf('|');
        if (sep < 0)
        {
            // Backward/forward tolerant: a bare integer is treated as a count with no duration.
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var only)
                ? new Sample(only, 0d)
                : default;
        }

        var countPart = raw.Substring(0, sep);
        var durationPart = raw.Substring(sep + 1);

        long.TryParse(countPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count);
        double.TryParse(durationPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var sumMs);
        return new Sample(count, sumMs);
    }

    /// <summary>
    /// Computes the week index of a UTC tick count as whole weeks since the Unix epoch
    /// (1970-01-01, a Thursday). Used as a stable, monotonic week bucket for trimming and grouping.
    /// </summary>
    private static long WeekIndex(long utcTicks)
    {
        var daysSinceEpoch = (utcTicks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerDay;
        // Floor toward negative infinity so pre-epoch instants (defensive) still bucket correctly.
        return (long)Math.Floor(daysSinceEpoch / 7d);
    }

    private static long AsUtcTicks(DateTime value) => AsUtc(value).Ticks;

    private static DateTime AsUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <inheritdoc />
    public void Dispose()
    {
        _stoppingCts?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>An accumulation bucket: a (week, queue, day-of-week, hour) coordinate.</summary>
    private readonly struct RollupBucket : IEquatable<RollupBucket>
    {
        public RollupBucket(long week, string queue, int dayOfWeek, int hour)
        {
            Week = week;
            Queue = queue ?? ScheduleAggregator.DefaultQueue;
            DayOfWeek = dayOfWeek;
            Hour = hour;
        }

        public long Week { get; }
        public string Queue { get; }
        public int DayOfWeek { get; }
        public int Hour { get; }

        public bool Equals(RollupBucket other)
            => Week == other.Week
               && DayOfWeek == other.DayOfWeek
               && Hour == other.Hour
               && string.Equals(Queue, other.Queue, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is RollupBucket other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                hash = (hash * 31) + Week.GetHashCode();
                hash = (hash * 31) + (Queue != null ? StringComparer.Ordinal.GetHashCode(Queue) : 0);
                hash = (hash * 31) + DayOfWeek;
                hash = (hash * 31) + Hour;
                return hash;
            }
        }
    }

    /// <summary>A packed rollup sample: an execution count and the summed duration in milliseconds.</summary>
    private readonly struct Sample
    {
        public Sample(long count, double sumDurationMs)
        {
            Count = count;
            SumDurationMs = sumDurationMs;
        }

        public long Count { get; }
        public double SumDurationMs { get; }
    }
}
