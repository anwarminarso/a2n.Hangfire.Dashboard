using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using a2n.Hangfire.Dashboard.Models;
using Microsoft.AspNetCore.Components;

namespace a2n.Hangfire.Dashboard.Components.Pages.Heatmap;

/// <summary>
/// Code-behind for the recurring-job table (task 11.6). It projects the recurring-job specs that
/// back the heatmap into display rows, counting each job's contributing cells from the aggregated
/// <see cref="HeatmapMatrix"/> and flagging the long-period jobs reported by the projection.
///
/// The table deliberately retains <em>every</em> recurring job — including long-period jobs that
/// contribute zero cells to the active <see cref="ProjectionWindow"/> (Req 9.7) — and renders an
/// empty-state when there are no recurring jobs at all (Req 9.1, 1.7). It never throws on missing
/// inputs: a <c>null</c> job list is treated as "no recurring jobs".
/// </summary>
public partial class RecurringJobTable
{
    /// <summary>
    /// The recurring jobs backing the projection, as mapped by <c>HeatmapService</c>. May be
    /// <c>null</c> or empty when none are configured or they could not be read (Req 1.7).
    /// </summary>
    [Parameter] public IReadOnlyList<RecurringJobSpec> Jobs { get; set; }

    /// <summary>
    /// The aggregated matrix for the active window, used only to count how many cells each job
    /// contributes. When <c>null</c> every job reports zero contributing cells.
    /// </summary>
    [Parameter] public HeatmapMatrix Matrix { get; set; }

    /// <summary>
    /// The identifiers of the long-period jobs (recurrence period &gt; 7 days) from
    /// <c>HeatmapResult.LongPeriodJobIds</c>. These rows are retained and flagged even when they
    /// contribute zero cells (Req 9.7).
    /// </summary>
    [Parameter] public IReadOnlyList<string> LongPeriodJobIds { get; set; }

    /// <summary>
    /// Optional collision-free queue → color map (the heatmap's available-queue palette). When
    /// supplied, queue badges use these colors so the table matches the chips and view legends.
    /// </summary>
    [Parameter] public IReadOnlyDictionary<string, string> QueueColorMap { get; set; }

    /// <summary>Resolves the badge color for a queue from the supplied map, or null to fall back.</summary>
    private string QueueColorFor(string queue) =>
        QueueColorMap is not null && queue is not null && QueueColorMap.TryGetValue(queue, out var color)
            ? color
            : null;

    /// <summary>The computed display rows, rebuilt whenever the parameters change.</summary>
    private IReadOnlyList<RowModel> Rows { get; set; } = Array.Empty<RowModel>();

    /// <summary>The number of long-period rows, surfaced in the table header.</summary>
    private int LongPeriodCount { get; set; }

    protected override void OnParametersSet()
    {
        Rows = BuildRows();
        LongPeriodCount = Rows.Count(r => r.IsLongPeriod);
    }

    /// <summary>
    /// Builds the display rows: one per recurring job, plus a defensive synthetic row for any
    /// long-period id that is not present in <see cref="Jobs"/> (so a long-period job is never lost,
    /// satisfying Req 9.7 even if the spec list and the notice diverge). Contributing-cell counts are
    /// derived from the matrix by counting the cells whose job-id set includes the job.
    /// </summary>
    private IReadOnlyList<RowModel> BuildRows()
    {
        var longPeriod = LongPeriodJobIds is { Count: > 0 }
            ? new HashSet<string>(LongPeriodJobIds, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var contributingCells = BuildContributingCellCounts();

        var rows = new List<RowModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (Jobs is { Count: > 0 })
        {
            foreach (var job in Jobs)
            {
                if (job is null)
                {
                    continue;
                }

                seen.Add(job.JobId);
                contributingCells.TryGetValue(job.JobId, out var cells);

                rows.Add(new RowModel(
                    job.JobId,
                    job.CronExpression,
                    string.IsNullOrWhiteSpace(job.Queue) ? "default" : job.Queue,
                    FormatTimeZone(job.TimeZoneId),
                    FormatDuration(job.EstimatedDuration),
                    cells,
                    longPeriod.Contains(job.JobId)));
            }
        }

        // Retain any long-period job that wasn't in the spec list so it is still acknowledged (Req 9.7).
        foreach (var jobId in longPeriod)
        {
            if (seen.Add(jobId))
            {
                contributingCells.TryGetValue(jobId, out var cells);
                rows.Add(new RowModel(jobId, null, "default", "UTC", "—", cells, IsLongPeriod: true));
            }
        }

        // Stable, predictable order: long-period jobs first, then by id (ordinal).
        return rows
            .OrderByDescending(r => r.IsLongPeriod)
            .ThenBy(r => r.JobId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Counts, per job id, the number of matrix cells the job contributes a fire to.</summary>
    private Dictionary<string, int> BuildContributingCellCounts()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (Matrix?.Cells is null)
        {
            return counts;
        }

        foreach (var cell in Matrix.Cells.Values)
        {
            if (cell.JobIds is null)
            {
                continue;
            }

            foreach (var jobId in cell.JobIds)
            {
                counts[jobId] = counts.TryGetValue(jobId, out var c) ? c + 1 : 1;
            }
        }

        return counts;
    }

    /// <summary>Formats the configured time zone for display; null/empty means UTC (Req 1.4, 8.3).</summary>
    private static string FormatTimeZone(string timeZoneId) =>
        string.IsNullOrWhiteSpace(timeZoneId) ? "UTC" : timeZoneId;

    /// <summary>Formats the estimated duration compactly (e.g. <c>2m</c>, <c>90s</c>, <c>1h 30m</c>).</summary>
    private static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return "—";
        }

        if (duration.TotalHours >= 1)
        {
            var hours = (int)duration.TotalHours;
            var minutes = duration.Minutes;
            return minutes > 0
                ? $"{hours}h {minutes}m"
                : $"{hours}h";
        }

        if (duration.TotalMinutes >= 1)
        {
            // Whole minutes render as e.g. "2m"; otherwise fall back to a one-decimal minute value.
            return duration.Seconds == 0
                ? $"{(int)duration.TotalMinutes}m"
                : duration.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture) + "m";
        }

        return $"{Math.Max(1, (int)Math.Round(duration.TotalSeconds))}s";
    }

    /// <summary>A single rendered row of the recurring-job table.</summary>
    /// <param name="JobId">The recurring job identifier.</param>
    /// <param name="CronExpression">The job's cron expression; <c>null</c> when unknown.</param>
    /// <param name="Queue">The resolved queue name.</param>
    /// <param name="TimeZoneLabel">The display label for the job's time zone (UTC when none).</param>
    /// <param name="DurationLabel">The formatted estimated duration.</param>
    /// <param name="ContributingCellCount">The number of active-window cells the job contributes to.</param>
    /// <param name="IsLongPeriod">Whether the job's recurrence period exceeds seven days (Req 9.7).</param>
    private sealed record RowModel(
        string JobId,
        string CronExpression,
        string Queue,
        string TimeZoneLabel,
        string DurationLabel,
        int ContributingCellCount,
        bool IsLongPeriod);
}
