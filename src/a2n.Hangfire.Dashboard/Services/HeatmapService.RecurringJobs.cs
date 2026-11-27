using System.Reflection;
using a2n.Hangfire.Dashboard.Heatmap;
using a2n.Hangfire.Dashboard.Models;
using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.Logging;

namespace a2n.Hangfire.Dashboard.Services;

public partial class HeatmapService
{
    /// <summary>
    /// Reads the recurring jobs via the storage-agnostic Hangfire connection and maps each
    /// <see cref="RecurringJobDto"/> onto a pure-engine <see cref="RecurringJobSpec"/>. Any storage
    /// failure is swallowed and yields an empty list so the page can render its empty state without
    /// raising an error (Req 1.7).
    /// </summary>
    /// <summary>
    /// Reads the recurring jobs via the storage-agnostic Hangfire connection. Any storage failure is
    /// swallowed and yields an empty list so the page can render its empty state without raising an
    /// error (Req 1.7).
    /// </summary>
    private List<RecurringJobDto> ReadRecurringJobDtos()
    {
        if (_storage == null)
        {
            return new List<RecurringJobDto>();
        }

        try
        {
            using var connection = _storage.GetConnection();
            var jobs = connection.GetRecurringJobs();
            if (jobs == null || jobs.Count == 0)
            {
                return new List<RecurringJobDto>();
            }

            var dtos = new List<RecurringJobDto>(jobs.Count);
            foreach (var dto in jobs)
            {
                if (dto != null)
                {
                    dtos.Add(dto);
                }
            }

            return dtos;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read recurring jobs for heatmap projection");
            return new List<RecurringJobDto>();
        }
    }

    /// <summary>
    /// Maps the supplied <see cref="RecurringJobDto"/> list onto pure-engine <see cref="RecurringJobSpec"/>s,
    /// resolving each job's estimated duration from the historical p95 supplied by the
    /// <see cref="EstimatedDurationResolver"/> when a metrics provider is registered (Req 21.2),
    /// otherwise the configured default treated as at least one minute (Req 21.3, 21.4). A single
    /// batched duration query is issued for the whole list; a missing resolver or any failure degrades
    /// every job to the flagged default, so the Projected source keeps working on any storage.
    /// </summary>
    private async Task<List<RecurringJobSpec>> BuildSpecsAsync(
        List<RecurringJobDto> dtos, CancellationToken ct)
    {
        var defaultDuration = ResolveDefaultDuration();

        if (dtos == null || dtos.Count == 0)
        {
            return new List<RecurringJobSpec>();
        }

        // Resolve historical p95 durations in one batched call keyed by the metrics provider's
        // "{ClassName}.{MethodName}" job-type key. Null resolver / no provider → empty map → defaults.
        var durations = await ResolveDurationsAsync(dtos, ct).ConfigureAwait(false);

        var specs = new List<RecurringJobSpec>(dtos.Count);
        foreach (var dto in dtos)
        {
            if (dto == null)
            {
                continue;
            }

            var queue = ResolveQueue(dto);
            var duration = defaultDuration;
            var isDefault = true;

            var key = JobTypeKey(dto);
            if (durations != null && key != null && durations.TryGetValue(key, out var resolved))
            {
                duration = resolved.Duration;
                isDefault = resolved.IsDefault;
            }

            specs.Add(new RecurringJobSpec(
                JobId: dto.Id,
                CronExpression: dto.Cron,
                TimeZoneId: dto.TimeZoneId,
                Queue: queue,
                EstimatedDuration: duration,
                EstimatedDurationIsDefault: isDefault));
        }

        return specs;
    }

    /// <summary>
    /// Resolves the job-type → estimated-duration map for the supplied jobs, memoized for the
    /// configured cache-TTL window so repeated projections within one interaction reuse a single
    /// duration-stats query. Returns <c>null</c> when no metrics provider is registered (every job
    /// then falls back to the configured default).
    /// </summary>
    private async Task<IReadOnlyDictionary<string, (TimeSpan Duration, bool IsDefault)>?> ResolveDurationsAsync(
        List<RecurringJobDto> dtos, CancellationToken ct)
    {
        if (_durationResolver == null || !_durationResolver.IsAvailable)
        {
            return null;
        }

        if (_durationMemo != null && DateTimeOffset.UtcNow - _durationMemoAt < _cacheTtl)
        {
            return _durationMemo;
        }

        var keys = dtos.Select(JobTypeKey).Where(k => !string.IsNullOrEmpty(k)).Select(k => k!);
        var (from, to) = DurationStatsWindow();

        try
        {
            var map = await _durationResolver.ResolveBatchAsync(keys, from, to, ct).ConfigureAwait(false);
            _durationMemo = map;
            _durationMemoAt = DateTimeOffset.UtcNow;
            return map;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve historical durations for the heatmap; using defaults");
            // Serve the previous memo when available; otherwise fall back to defaults (null).
            return _durationMemo;
        }
    }

    /// <summary>
    /// Derives the metrics provider's job-type key (<c>{ClassName}.{MethodName}</c>) from a recurring
    /// job, matching <c>IStorageMetricsProvider.GetJobDurationStatsAsync</c>'s <c>JobType</c> format.
    /// Returns <c>null</c> when the job's type or method cannot be determined.
    /// </summary>
    private static string? JobTypeKey(RecurringJobDto dto)
    {
        var type = dto?.Job?.Type;
        var method = dto?.Job?.Method;
        if (type == null || method == null)
        {
            return null;
        }

        return $"{type.Name}.{method.Name}";
    }

    /// <summary>
    /// The historical window over which job-type durations are sampled for the p95 estimate. A short,
    /// fixed 7-day lookback keeps the duration query bounded and independent of the projection window.
    /// </summary>
    private static (DateTimeOffset From, DateTimeOffset To) DurationStatsWindow()
    {
        var to = DateTimeOffset.UtcNow;
        return (to.AddDays(-7), to);
    }

    /// <summary>
    /// Determines a recurring job's effective queue. Precedence: the job's own InvocationData queue
    /// (set by the modern <c>RecurringJob.AddOrUpdate(id, queue, …)</c> API and used by Hangfire when
    /// the job is enqueued), then the recurring job's top-level stored queue (older API), then a
    /// <see cref="QueueAttribute"/> on the method (or its declaring type), and finally
    /// <see cref="ScheduleAggregator.DefaultQueue"/> when none is determinable (Req 2.4).
    /// </summary>
    private static string ResolveQueue(RecurringJobDto dto)
    {
        var job = dto.Job;

        // A [Queue] attribute on the method (or its declaring type — e.g. an interface carrying
        // [Queue("background")] for a DI-dispatched job) is what actually routes the job at enqueue
        // time, so it takes precedence. This matches EffectiveQueue.Resolve (Req 13.6, 13.7) used on
        // the create/enqueue paths. Hangfire writes the "default" sentinel into the stored queue
        // fields when no explicit queue was given, so consulting the stored value first (as before)
        // masked attribute-routed queues and showed "default" on the planner (issue #14 follow-up).
        if (job != null)
        {
            try
            {
                var attr = job.Method?.GetCustomAttribute<QueueAttribute>(inherit: true)
                           ?? job.Method?.DeclaringType?.GetCustomAttribute<QueueAttribute>(inherit: true);

                if (attr != null && !string.IsNullOrWhiteSpace(attr.Queue) && !attr.Queue.Contains('{'))
                {
                    return attr.Queue;
                }
            }
            catch
            {
                // Reflection failure → fall through to the stored/explicit queue.
            }
        }

        // No applicable attribute. Prefer the job's own InvocationData queue: the modern Hangfire API
        // `RecurringJob.AddOrUpdate(id, queue, …)` stores the target queue here (serialized as "q").
        if (job != null && !string.IsNullOrWhiteSpace(job.Queue))
        {
            return job.Queue;
        }

        // Older API path: the queue was stored in the recurring job's top-level "Queue" field.
        if (!string.IsNullOrWhiteSpace(dto.Queue))
        {
            return dto.Queue;
        }

        return ScheduleAggregator.DefaultQueue;
    }

    /// <summary>
    /// Returns the configured default estimated duration, treated as at least one minute (Req 21.3).
    /// </summary>
    private TimeSpan ResolveDefaultDuration()
    {
        var configured = _options.Heatmap?.DefaultEstimatedDuration ?? TimeSpan.FromMinutes(1);
        return configured < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : configured;
    }

    /// <summary>
    /// Builds the empty-state result: an empty matrix over the active window with no notices (Req 1.5, 1.7).
    /// </summary>
    private static HeatmapResult EmptyResult(ProjectionWindow window, LoadMetric metric)
    {
        var matrix = new HeatmapMatrix(
            Cells: new Dictionary<CellKey, HeatmapCell>(),
            Queues: Array.Empty<string>(),
            Window: window,
            Metric: metric,
            Min: 0,
            Max: 0);

        return new HeatmapResult(
            matrix,
            UnparseableJobIds: Array.Empty<string>(),
            UnknownTimeZoneJobIds: Array.Empty<string>(),
            LongPeriodJobIds: Array.Empty<string>(),
            HistoricalError: null);
    }

    /// <summary>
    /// A cached aggregation together with the instant at which it stops being fresh. The entry is
    /// retained physically beyond this instant so it can be served stale while a background refresh
    /// recomputes it (Req 13.5, 13.8).
    /// </summary>
    private sealed class CacheEntry
    {
        public CacheEntry(HeatmapResult value, DateTimeOffset expiresAtUtc)
        {
            Value = value;
            ExpiresAtUtc = expiresAtUtc;
        }

        /// <summary>The cached aggregation result.</summary>
        public HeatmapResult Value { get; }

        /// <summary>The UTC instant after which the entry is considered stale (Req 13.5).</summary>
        public DateTimeOffset ExpiresAtUtc { get; }
    }
}
