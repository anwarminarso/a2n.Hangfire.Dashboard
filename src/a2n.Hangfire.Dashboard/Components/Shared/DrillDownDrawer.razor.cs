using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using a2n.Hangfire.Dashboard.Models;
using Microsoft.AspNetCore.Components;

namespace a2n.Hangfire.Dashboard.Components.Shared;

/// <summary>
/// Code-behind for the drill-down drawer (task 20.1). The component is purely presentational: it
/// receives an already-computed <see cref="DrillDownResult"/> from the parent (the
/// <c>HeatmapService.GetCellJobsAsync</c> wiring is task 20.2) and renders the contributing
/// recurring jobs with their per-job actions.
///
/// Key behaviors (Req 10.1–10.7):
/// <list type="bullet">
///   <item>Opens only when there is something to show — an error to surface or at least one
///   contributing job — so it never opens for an empty cell (Req 10.1, 10.2).</item>
///   <item>Lists jobs sorted by next run ascending, with nulls (no known next run) last
///   (Req 10.1).</item>
///   <item>Shows the id, cron, queue, estimated duration in seconds, and next run in the configured
///   display time zone for each job (Req 10.3).</item>
///   <item>Always offers a "view executions" action (Req 10.6); offers an "edit schedule" action
///   reusing <c>ScheduleBuilder</c> only when <see cref="DashboardUIOptions.EnableJobManagement"/>
///   is enabled and <see cref="DashboardUIOptions.IsReadOnly"/> is disabled (Req 10.4, 10.5).</item>
///   <item>Surfaces an error indication when <see cref="DrillDownResult.Error"/> is set; the parent
///   retains the previously displayed heatmap unchanged (Req 10.7).</item>
/// </list>
/// </summary>
public partial class DrillDownDrawer
{
    /// <summary>
    /// Whether the parent wants the drawer open. The drawer additionally self-guards via
    /// <see cref="IsOpen"/> so it never opens for an empty cell even if the parent toggles this on.
    /// </summary>
    [Parameter] public bool Visible { get; set; }

    /// <summary>
    /// The computed drill-down result for the clicked cell: the contributing jobs (which the drawer
    /// re-sorts defensively) plus an optional error message. <c>null</c> while nothing is loaded.
    /// </summary>
    [Parameter] public DrillDownResult Result { get; set; }

    /// <summary>An optional human-readable label for the clicked cell (e.g. "Mon · 09:00 · default").</summary>
    [Parameter] public string CellLabel { get; set; }

    /// <summary>
    /// The dashboard's configured display time zone used to render each job's next run (Req 10.3).
    /// Defaults to UTC when not supplied.
    /// </summary>
    [Parameter] public TimeZoneInfo DisplayTimeZone { get; set; }

    /// <summary>The label shown next to converted next-run times. Defaults to "UTC".</summary>
    [Parameter] public string DisplayTimeZoneLabel { get; set; } = "UTC";

    /// <summary>Raised when the operator dismisses the drawer.</summary>
    [Parameter] public EventCallback OnClose { get; set; }

    /// <summary>Raised when the operator requests to view a job's executions (Req 10.6).</summary>
    [Parameter] public EventCallback<DrillDownJob> OnViewExecutions { get; set; }

    /// <summary>
    /// Raised when the operator saves an edited schedule (Req 10.4). Persistence is the parent's
    /// responsibility; the drawer only collects the new cron via the reused <c>ScheduleBuilder</c>.
    /// </summary>
    [Parameter] public EventCallback<ScheduleEditRequest> OnSaveSchedule { get; set; }

    /// <summary>The id of the job whose inline schedule editor is currently expanded, if any.</summary>
    private string _editingJobId;

    /// <summary>The latest cron/validity emitted by the inline <c>ScheduleBuilder</c>.</summary>
    private ScheduleBuilder.ScheduleState _editState = new(string.Empty, false);

    /// <summary>True when there is an error to surface for the clicked cell (Req 10.7).</summary>
    private bool HasError => Result is not null && !string.IsNullOrWhiteSpace(Result.Error);

    /// <summary>
    /// The drawer opens only when it has something meaningful to show: an error, or at least one
    /// contributing job. This enforces "never open for an empty cell" independently of the parent
    /// (Req 10.1, 10.2).
    /// </summary>
    private bool IsOpen => Visible && Result is not null && (HasError || SortedJobs.Count > 0);

    /// <summary>Edit actions are only offered when job management is enabled and not read-only (Req 10.4, 10.5).</summary>
    private bool CanEdit => Options.EnableJobManagement && !Options.IsReadOnly;

    /// <summary>
    /// The contributing jobs sorted by next run ascending, with jobs that have no known next run
    /// ordered last and then by id for a stable, deterministic order (Req 10.1).
    /// </summary>
    private IReadOnlyList<DrillDownJob> SortedJobs
    {
        get
        {
            var jobs = Result?.Jobs;
            if (jobs is null || jobs.Count == 0)
            {
                return Array.Empty<DrillDownJob>();
            }

            return jobs
                .Where(j => j is not null)
                .OrderBy(j => j.NextRunUtc.HasValue ? 0 : 1)
                .ThenBy(j => j.NextRunUtc ?? DateTimeOffset.MaxValue)
                .ThenBy(j => j.JobId, StringComparer.Ordinal)
                .ToList();
        }
    }

    protected override void OnParametersSet()
    {
        // Reset any in-progress edit when the drawer is closed or a different cell is loaded so the
        // editor never lingers against a stale job.
        if (!IsOpen)
        {
            _editingJobId = null;
        }
    }

    private bool IsEditing(string jobId) => string.Equals(_editingJobId, jobId, StringComparison.Ordinal);

    private void ToggleEdit(string jobId)
    {
        if (IsEditing(jobId))
        {
            _editingJobId = null;
        }
        else
        {
            _editingJobId = jobId;
            _editState = new ScheduleBuilder.ScheduleState(string.Empty, false);
        }
    }

    private void HandleScheduleChanged(ScheduleBuilder.ScheduleState state) => _editState = state;

    private async Task CloseAsync()
    {
        _editingJobId = null;
        if (OnClose.HasDelegate)
        {
            await OnClose.InvokeAsync();
        }
    }

    private async Task ViewExecutionsAsync(DrillDownJob job)
    {
        if (OnViewExecutions.HasDelegate)
        {
            await OnViewExecutions.InvokeAsync(job);
        }
    }

    private async Task SaveScheduleAsync(DrillDownJob job)
    {
        if (!_editState.IsValid)
        {
            return;
        }

        if (OnSaveSchedule.HasDelegate)
        {
            await OnSaveSchedule.InvokeAsync(new ScheduleEditRequest(job.JobId, _editState.Cron));
        }

        _editingJobId = null;
    }

    /// <summary>Formats the estimated duration in whole seconds, treated as at least 1 second (Req 10.3).</summary>
    private static string FormatDurationSeconds(TimeSpan duration)
    {
        var seconds = (long)Math.Round(duration.TotalSeconds, MidpointRounding.AwayFromZero);
        if (seconds < 1)
        {
            seconds = 1;
        }

        return seconds.ToString(CultureInfo.InvariantCulture) + " s";
    }

    /// <summary>
    /// Converts the next run instant into the configured display time zone (Req 10.3). Returns a
    /// neutral placeholder when the job has no known next run.
    /// </summary>
    private string FormatNextRun(DateTimeOffset? nextRunUtc)
    {
        if (nextRunUtc is null)
        {
            return "—";
        }

        var zone = DisplayTimeZone ?? TimeZoneInfo.Utc;
        var local = TimeZoneInfo.ConvertTime(nextRunUtc.Value, zone);
        return $"{local:yyyy-MM-dd HH:mm:ss} {DisplayTimeZoneLabel}";
    }

    /// <summary>
    /// A request to persist an edited schedule, emitted by <see cref="OnSaveSchedule"/>. Carries the
    /// affected job id and the new cron expression produced by the reused <c>ScheduleBuilder</c>.
    /// </summary>
    /// <param name="JobId">The recurring job whose schedule was edited.</param>
    /// <param name="Cron">The new cron expression to persist.</param>
    public sealed record ScheduleEditRequest(string JobId, string Cron);
}
