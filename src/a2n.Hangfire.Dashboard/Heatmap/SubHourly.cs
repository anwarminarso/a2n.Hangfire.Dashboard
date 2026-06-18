using System;
using System.Collections.Generic;
using System.Linq;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Heatmap;

/// <summary>
/// Pure, deterministic detection and filtering of <em>sub-hourly</em> recurring jobs. A job is
/// classified as sub-hourly when some single clock hour of the active projection window contains
/// more than one of that job's projected fires (e.g. a <c>*/5</c> or <c>*/15</c> cron). The
/// "Hide sub-hourly" control removes every fire belonging to a sub-hourly job from the matrix,
/// concurrency, and recommendation inputs so that high-frequency jobs do not drown out meaningful
/// schedule patterns.
/// </summary>
/// <remarks>
/// <para>Bucketing reuses <see cref="HeatmapTime.GetBucket"/> so the <c>(dayIndex, hour)</c>
/// assignment is identical to <see cref="ScheduleAggregator"/>: a job is sub-hourly when, for some
/// <c>(dayIndex, hour)</c> bucket, it contributes more than one fire. Both operations are
/// order-independent and produce deterministic output.</para>
/// <para>Validates Requirements 20.1, 20.2, and 20.3.</para>
/// </remarks>
public static class SubHourly
{
    /// <summary>
    /// Detects the identifiers of every sub-hourly job among the supplied fires. A job is sub-hourly
    /// when more than one of its fires falls into the same <c>(dayIndex, hour)</c> bucket within the
    /// active window (Req 20.1).
    /// </summary>
    /// <param name="fires">The projected fires to classify; order does not affect the result.</param>
    /// <param name="viewerTimeZone">The viewer time zone fires are converted to; UTC when null.</param>
    /// <param name="window">The active projection window the buckets are measured against.</param>
    /// <returns>
    /// The distinct, ascending-ordered set of job identifiers that fire more than once within some
    /// clock hour of the window. Empty when no fires are supplied or none are sub-hourly.
    /// </returns>
    public static IReadOnlyCollection<string> DetectSubHourlyJobIds(
        IReadOnlyList<ProjectedFire> fires,
        TimeZoneInfo viewerTimeZone,
        ProjectionWindow window)
    {
        if (window is null)
        {
            throw new ArgumentNullException(nameof(window));
        }

        var subHourlyJobIds = new SortedSet<string>(StringComparer.Ordinal);

        if (fires is null || fires.Count == 0)
        {
            return subHourlyJobIds;
        }

        var timeZone = viewerTimeZone ?? TimeZoneInfo.Utc;

        // Count, per (jobId, dayIndex, hour) bucket, how many fires the job contributes. The first
        // bucket that exceeds one fire marks the job as sub-hourly.
        var firesPerJobBucket = new Dictionary<JobBucketKey, int>();

        foreach (var fire in fires)
        {
            if (fire is null)
            {
                continue;
            }

            var jobId = fire.JobId ?? string.Empty;

            // Already known to be sub-hourly; no need to keep counting its buckets.
            if (subHourlyJobIds.Contains(jobId))
            {
                continue;
            }

            var (dayIndex, hour) = HeatmapTime.GetBucket(fire.FireTimeUtc, timeZone, window);
            var key = new JobBucketKey(jobId, dayIndex, hour);

            firesPerJobBucket.TryGetValue(key, out var count);
            count++;
            firesPerJobBucket[key] = count;

            if (count > 1)
            {
                subHourlyJobIds.Add(jobId);
            }
        }

        return subHourlyJobIds;
    }

    /// <summary>
    /// Returns the supplied fires with every fire belonging to a sub-hourly job removed, suitable for
    /// feeding matrix, concurrency, and recommendation computations when "Hide sub-hourly" is enabled
    /// (Req 20.2). When no job is sub-hourly the original fires are returned unchanged (Req 20.3 is
    /// satisfied by simply not invoking the filter).
    /// </summary>
    /// <param name="fires">The projected fires to filter; order is preserved among retained fires.</param>
    /// <param name="viewerTimeZone">The viewer time zone fires are converted to; UTC when null.</param>
    /// <param name="window">The active projection window the buckets are measured against.</param>
    /// <returns>
    /// A new list containing only the fires whose job is not sub-hourly, in their original relative
    /// order. Empty when all supplied fires belong to sub-hourly jobs.
    /// </returns>
    public static IReadOnlyList<ProjectedFire> Filter(
        IReadOnlyList<ProjectedFire> fires,
        TimeZoneInfo viewerTimeZone,
        ProjectionWindow window)
    {
        if (window is null)
        {
            throw new ArgumentNullException(nameof(window));
        }

        if (fires is null || fires.Count == 0)
        {
            return new List<ProjectedFire>();
        }

        var subHourlyJobIds = DetectSubHourlyJobIds(fires, viewerTimeZone, window);

        if (subHourlyJobIds.Count == 0)
        {
            return fires.Where(f => f is not null).ToList();
        }

        var excluded = new HashSet<string>(subHourlyJobIds, StringComparer.Ordinal);
        var retained = new List<ProjectedFire>(fires.Count);

        foreach (var fire in fires)
        {
            if (fire is null)
            {
                continue;
            }

            if (!excluded.Contains(fire.JobId ?? string.Empty))
            {
                retained.Add(fire);
            }
        }

        return retained;
    }

    /// <summary>
    /// Composite key identifying a single job's fires within one <c>(dayIndex, hour)</c> bucket.
    /// </summary>
    private readonly struct JobBucketKey : IEquatable<JobBucketKey>
    {
        private readonly string _jobId;
        private readonly int _dayIndex;
        private readonly int _hour;

        public JobBucketKey(string jobId, int dayIndex, int hour)
        {
            _jobId = jobId;
            _dayIndex = dayIndex;
            _hour = hour;
        }

        public bool Equals(JobBucketKey other)
            => _dayIndex == other._dayIndex
               && _hour == other._hour
               && string.Equals(_jobId, other._jobId, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is JobBucketKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(_jobId ?? string.Empty);
                hash = (hash * 397) ^ _dayIndex;
                hash = (hash * 397) ^ _hour;
                return hash;
            }
        }
    }
}
