using a2n.Hangfire.Dashboard.Components.Shared;
using a2n.Hangfire.Dashboard.Heatmap;
using a2n.Hangfire.Dashboard.Internal;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services;
using Microsoft.JSInterop;

namespace a2n.Hangfire.Dashboard.Components.Pages.Heatmap;

public partial class Heatmap
{
    private void DismissHistoricalError() => _historicalErrorDismissed = true;

    // ── Drill-down drawer wiring (task 20.2) ─────────────────────────────────────────────────────

    /// <summary>
    /// The viewer time zone used to render each drill-down job's next run (Req 10.3). The page's
    /// "View TZ" selection is the dashboard's active display time zone; empty means UTC (Req 8.5).
    /// </summary>
    private TimeZoneInfo DrawerDisplayTimeZone => HeatmapTime.ResolveTimeZone(_viewerTzId);

    /// <summary>The label shown beside converted next-run times (the selected tz id, or "UTC").</summary>
    private string DrawerDisplayTimeZoneLabel => string.IsNullOrWhiteSpace(_viewerTzId) ? "UTC" : _viewerTzId;

    /// <summary>
    /// A selectable drill-down target: a single matrix cell with one or more contributing recurring
    /// jobs. Only such cells are offered so the drawer is never opened for an empty cell (Req 10.1, 10.2).
    /// </summary>
    private sealed record DrillableCell(string Id, string Label, CellKey Key);

    /// <summary>
    /// The populated cells the operator can drill into, ordered by day, hour, then queue. Built from
    /// the aggregated matrix; cells with zero contributing jobs are excluded (Req 10.1, 10.2).
    /// </summary>
    private IReadOnlyList<DrillableCell> DrillableCells
    {
        get
        {
            var matrix = _result?.Matrix;
            if (matrix is null || matrix.Cells.Count == 0)
            {
                return Array.Empty<DrillableCell>();
            }

            return matrix.Cells.Values
                .Where(c => c is not null && c.ContributingJobCount > 0)
                .OrderBy(c => c.Key.DayIndex)
                .ThenBy(c => c.Key.Hour)
                .ThenBy(c => c.Key.Queue, StringComparer.Ordinal)
                .Select(c => new DrillableCell(
                    Id: $"{c.Key.Queue}|{c.Key.DayIndex}|{c.Key.Hour}",
                    Label: CellLabel(c.Key, c.ContributingJobCount),
                    Key: c.Key))
                .ToList();
        }
    }

    /// <summary>Builds a human-readable label for a cell, e.g. "Mon · 09:00 · default (3 jobs)".</summary>
    private static string CellLabel(CellKey key, int jobCount)
    {
        var day = key.DayIndex >= 0 && key.DayIndex < DayLabels.Length
            ? DayLabels[key.DayIndex]
            : $"Day {key.DayIndex + 1}";
        return $"{day} · {key.Hour:D2}:00 · {key.Queue} ({jobCount} {(jobCount == 1 ? "job" : "jobs")})";
    }

    /// <summary>
    /// A [JSInvokable] hook the heatmap.js cell-click wiring calls (via the DotNetObjectReference
    /// registered in <see cref="OnAfterRenderAsync"/>) to open the drawer for a clicked cell, driving
    /// the <see cref="OpenCellAsync"/> path. Also used to open the drawer for a clicked recommendation.
    /// </summary>
    /// <param name="queue">The clicked cell's queue.</param>
    /// <param name="dayIndex">The clicked cell's zero-based day index within the window.</param>
    /// <param name="hour">The clicked cell's clock hour (0–23).</param>
    [JSInvokable]
    public async Task OpenCellDrawerAsync(string queue, int dayIndex, int hour)
    {
        var key = new CellKey(queue, dayIndex, hour);
        // Prefer the populated-cell label when the cell is known; otherwise synthesize one.
        var label = DrillableCells.FirstOrDefault(c =>
            string.Equals(c.Key.Queue, queue, StringComparison.Ordinal)
            && c.Key.DayIndex == dayIndex
            && c.Key.Hour == hour)?.Label
            ?? CellLabel(key, 0);

        await OpenCellAsync(key, label);
        StateHasChanged();
    }

    /// <summary>
    /// Resolves the contributing recurring jobs for <paramref name="key"/> via
    /// <see cref="HeatmapService.GetCellJobsAsync"/> and opens the drawer. On a load failure the
    /// service returns a <see cref="DrillDownResult"/> with an error (never throws), which the drawer
    /// surfaces while the page keeps its heatmap data unchanged (Req 10.7). The drawer self-guards
    /// against opening for an empty (no-job, no-error) cell (Req 10.1, 10.2).
    /// </summary>
    private async Task OpenCellAsync(CellKey key, string label)
    {
        _drillDownLoading = true;
        _drawerCellLabel = label;
        StateHasChanged();

        try
        {
            _drillDown = await HeatmapSvc.GetCellJobsAsync(key, BuildQuery(), CancellationToken.None);
        }
        finally
        {
            _drillDownLoading = false;
            // Toggle visibility on; the drawer only actually opens when it has an error or ≥ 1 job.
            _drawerVisible = true;
            StateHasChanged();
        }
    }

    /// <summary>Opens the drill-down drawer for a clicked Planner cell (attributed to its dominant queue).</summary>
    private Task OnPlannerCellInspectAsync(PlannerView.PlannerCellSelection selection) =>
        selection is null
            ? Task.CompletedTask
            : OpenCellDrawerAsync(selection.Queue, selection.DayIndex, selection.Hour);

    /// <summary>
    /// Opens the drill-down drawer for a clicked recommendation, resolving its cell from the
    /// recommendation's queue, peak hour, and the first window day that matches its weekday cluster.
    /// </summary>
    private async Task OpenRecommendationAsync(Recommendation rec)
    {
        if (rec is null)
        {
            return;
        }

        var hour = Math.Clamp(rec.PeakMinuteOfDay / 60, 0, 23);
        var dayIndex = ResolveRecommendationDayIndex(rec);
        var key = new CellKey(rec.Queue, dayIndex, hour);

        var label = DrillableCells.FirstOrDefault(c =>
            string.Equals(c.Key.Queue, rec.Queue, StringComparison.Ordinal)
            && c.Key.DayIndex == dayIndex
            && c.Key.Hour == hour)?.Label
            ?? CellLabel(key, 0);

        await OpenCellAsync(key, label);
    }

    /// <summary>
    /// Maps a recommendation's weekday cluster to the first matching window day index, so its
    /// drill-down resolves to a concrete <see cref="CellKey"/>. Falls back to day 0.
    /// </summary>
    private int ResolveRecommendationDayIndex(Recommendation rec)
    {
        if (_result is null || rec?.Weekdays is null || rec.Weekdays.Count == 0)
        {
            return 0;
        }

        var tz = HeatmapTime.ResolveTimeZone(_viewerTzId);
        var window = _result.Matrix.Window;
        var wanted = new HashSet<DayOfWeek>(rec.Weekdays);

        for (var d = 0; d < HeatmapTime.WindowDays; d++)
        {
            // MapDayIndexToDayOfWeek returns 0..6 (0 = Sunday), matching (int)DayOfWeek.
            if (wanted.Contains((DayOfWeek)PlannerHelpers.MapDayIndexToDayOfWeek(window, tz, d)))
            {
                return d;
            }
        }

        return 0;
    }

    /// <summary>Dismisses the drawer, leaving the heatmap unchanged.</summary>
    private void CloseDrawer()
    {
        _drawerVisible = false;
    }

    /// <summary>
    /// Navigates to the recurring jobs page where the selected job's executions are listed (Req 10.6).
    /// The href is relative so it is correctly prefixed by any configured AppPath.
    /// </summary>
    private void ViewExecutions(DrillDownJob job)
    {
        CloseDrawer();
        Navigation.NavigateTo("recurring");
    }

    /// <summary>
    /// Persists an inline schedule edit from the drawer (Req 10.4). The new cron is applied to the
    /// existing recurring job through the gated <see cref="HangfireMonitorService"/> upsert (which
    /// re-checks read-only / job-management), preserving the job's type, method, arguments, queue, and
    /// time zone. On success the matrix is recomputed; on failure a dismissible notice is surfaced and
    /// the heatmap is left unchanged.
    /// </summary>
    private async Task SaveScheduleAsync(DrillDownDrawer.ScheduleEditRequest request)
    {
        _drawerSaveError = null;

        if (request is null || string.IsNullOrWhiteSpace(request.JobId))
        {
            return;
        }

        try
        {
            var existing = Monitor.GetRecurringJobs()?.FirstOrDefault(j => j.Id == request.JobId);
            if (existing is null)
            {
                _drawerSaveError = $"Recurring job '{request.JobId}' could not be found; it may have been removed.";
                return;
            }

            var parameterJson = "[]";
            try
            {
                if (existing.Job?.Method is not null)
                {
                    parameterJson = JobArgumentConverter.ToParameterJsonFromArgs(existing.Job.Method, existing.Job.Args);
                }
            }
            catch
            {
                // Fall back to an empty argument array rather than blocking the cron change.
                parameterJson = "[]";
            }

            var req = new RecurringJobRequest(
                JobId: existing.Id,
                TypeName: existing.Job?.Type?.FullName ?? string.Empty,
                MethodName: existing.Job?.Method?.Name ?? string.Empty,
                ParameterJson: parameterJson,
                Cron: request.Cron,
                Queue: existing.Queue ?? string.Empty,
                TimeZoneId: existing.TimeZoneId ?? string.Empty,
                IsCustomMethod: false);

            var result = await Monitor.CreateOrUpdateRecurringJobAsync(req);
            if (!result.Success)
            {
                _drawerSaveError = result.Error ?? "The schedule could not be saved.";
                return;
            }

            CloseDrawer();
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _drawerSaveError = "The schedule could not be saved: " + ex.Message;
        }
    }
}
