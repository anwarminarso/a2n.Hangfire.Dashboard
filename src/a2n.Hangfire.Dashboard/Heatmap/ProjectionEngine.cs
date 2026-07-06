using System;
using System.Collections.Generic;
using a2n.Hangfire.Dashboard.Internal;
using a2n.Hangfire.Dashboard.Models;
using Cronos;

namespace a2n.Hangfire.Dashboard.Heatmap;

/// <summary>
/// Pure, storage-agnostic projection of recurring-job schedules into concrete fire times over a
/// bounded window. The engine evaluates each job's cron expression with the bundled Cronos library
/// in the job's configured time zone (UTC when none is configured), normalizes every fire to an
/// absolute UTC instant, and keeps only the fires that fall inside the window's half-open
/// <c>[StartInclusive, EndExclusive)</c> interval.
/// </summary>
/// <remarks>
/// <para>The engine never touches storage: it consumes a list of <see cref="RecurringJobSpec"/>
/// primitives (mapped from Hangfire's <c>RecurringJobDto</c> by the service layer) so it is trivially
/// testable and decoupled from Hangfire model changes (Req 1.2).</para>
/// <para>It degrades gracefully per job: a job with an unparseable cron is excluded and recorded
/// (Req 1.6); a job with an unrecognized time-zone id is evaluated in UTC and recorded (Req 8.6); a
/// job whose recurrence period exceeds seven days is recorded as a long-period job (Req 9.5). In all
/// cases the remaining jobs' fires are unaffected.</para>
/// <para>Validates portions of Requirements 1.1, 1.2, 1.3, 1.4, 1.6, 8.1, 8.3, 8.6, and 9.5.</para>
/// </remarks>
public static class ProjectionEngine
{
    /// <summary>The minimum permitted projection-window duration (Req 1.1).</summary>
    private static readonly TimeSpan MinWindowDuration = TimeSpan.FromHours(1);

    /// <summary>The maximum permitted projection-window duration (Req 1.1).</summary>
    private static readonly TimeSpan MaxWindowDuration = TimeSpan.FromDays(366);

    /// <summary>
    /// A job whose gap between consecutive fires exceeds seven days cannot be faithfully represented
    /// in a seven-day window and is classified as a long-period job (Req 9.5).
    /// </summary>
    private static readonly TimeSpan LongPeriodThreshold = TimeSpan.FromDays(HeatmapTime.WindowDays);

    /// <summary>
    /// Projects every supplied recurring job into its in-window fire times, honoring each job's time
    /// zone, and collects the per-job diagnostics (unparseable crons, unknown time zones, long-period
    /// jobs).
    /// </summary>
    /// <param name="jobs">The recurring jobs to project; a null or empty list yields an empty result.</param>
    /// <param name="window">The active projection window. Its duration must be at least 1 hour and at most 366 days (Req 1.1).</param>
    /// <returns>
    /// A <see cref="ProjectionResult"/> carrying the union of all parseable jobs' in-window fires and
    /// the identifiers of jobs excluded for an unparseable cron, evaluated in UTC for an unknown time
    /// zone, or detected as long-period.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="window"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The window duration is outside [1 hour, 366 days] (Req 1.1).</exception>
    public static ProjectionResult Project(IReadOnlyList<RecurringJobSpec> jobs, ProjectionWindow window)
    {
        if (window is null)
        {
            throw new ArgumentNullException(nameof(window));
        }

        var duration = window.EndExclusive - window.StartInclusive;
        if (duration < MinWindowDuration || duration > MaxWindowDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(window),
                duration,
                "The projection window duration must be at least 1 hour and at most 366 days.");
        }

        var fires = new List<ProjectedFire>();
        var unparseableJobIds = new List<string>();
        var unknownTimeZoneJobIds = new List<string>();
        var longPeriodJobIds = new List<string>();

        if (jobs is null || jobs.Count == 0)
        {
            return new ProjectionResult(fires, unparseableJobIds, unknownTimeZoneJobIds, longPeriodJobIds);
        }

        foreach (var job in jobs)
        {
            if (job is null)
            {
                continue;
            }

            // Exclude only the offending job when its cron cannot be parsed; record its id (Req 1.6).
            if (!CronPreview.TryParse(job.CronExpression, out var cron))
            {
                unparseableJobIds.Add(job.JobId);
                continue;
            }

            // Evaluate in the job's configured time zone (Req 1.3, 8.1); fall back to UTC when there
            // is no configured zone (Req 1.4, 8.3) or the configured id is unrecognized (Req 8.6).
            var zone = ResolveJobTimeZone(job, unknownTimeZoneJobIds);

            // In-window fires over the half-open [start, end) interval, normalized to absolute UTC.
            foreach (var occurrence in cron.GetOccurrences(
                         window.StartInclusive,
                         window.EndExclusive,
                         zone,
                         fromInclusive: true,
                         toInclusive: false))
            {
                fires.Add(new ProjectedFire(
                    job.JobId,
                    job.Queue,
                    occurrence.ToUniversalTime(),
                    job.EstimatedDuration));
            }

            // A job whose period exceeds seven days is recorded so the UI can warn it is not
            // faithfully represented in the window (Req 9.5).
            if (IsLongPeriod(cron, zone, window))
            {
                longPeriodJobIds.Add(job.JobId);
            }
        }

        return new ProjectionResult(fires, unparseableJobIds, unknownTimeZoneJobIds, longPeriodJobIds);
    }

    /// <summary>
    /// Resolves the time zone a job's schedule is evaluated in. A null/empty time-zone id means UTC
    /// (Req 1.4, 8.3) and is not treated as an error; a non-empty but unrecognized id falls back to
    /// UTC and the job id is recorded in <paramref name="unknownTimeZoneJobIds"/> (Req 8.6).
    /// </summary>
    private static TimeZoneInfo ResolveJobTimeZone(RecurringJobSpec job, ICollection<string> unknownTimeZoneJobIds)
    {
        if (string.IsNullOrWhiteSpace(job.TimeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        if (HeatmapTime.TryResolveTimeZone(job.TimeZoneId, out var resolved))
        {
            return resolved;
        }

        unknownTimeZoneJobIds.Add(job.JobId);
        return TimeZoneInfo.Utc;
    }

    /// <summary>
    /// Detects a long-period job by probing the first two occurrences at/after the window start: when
    /// the gap between consecutive fires exceeds seven days (or the job fires only once ever), it
    /// cannot be faithfully represented in a seven-day window (Req 9.5). A job that never fires is not
    /// classified as long-period.
    /// </summary>
    private static bool IsLongPeriod(CronExpression cron, TimeZoneInfo zone, ProjectionWindow window)
    {
        var first = cron.GetNextOccurrence(window.StartInclusive, zone, inclusive: true);
        if (first is null)
        {
            return false;
        }

        var second = cron.GetNextOccurrence(first.Value, zone, inclusive: false);
        if (second is null)
        {
            // The job fires at most once, so its effective period exceeds any bounded window.
            return true;
        }

        return (second.Value - first.Value) > LongPeriodThreshold;
    }
}
