using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using global::Hangfire.Storage.Monitoring;
using global::Hangfire.Storage;
using a2n.Hangfire.Dashboard.Helpers;
using a2n.Hangfire.Dashboard.Security;
using a2n.Hangfire.Dashboard.Services;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using global::Hangfire.Common;
using global::Hangfire.States;
using Microsoft.Extensions.DependencyInjection;

namespace a2n.Hangfire.Dashboard.Components.Pages.Jobs;

public partial class Failed
{
    // Read-only for this page load: DashboardUIOptions.IsReadOnly, or the permissions DashboardRoutes
    // cascades (absent only when a component is rendered on its own, as in tests).
    [CascadingParameter] private DashboardPermissions? Permissions { get; set; }
    private bool IsReadOnly => DashboardPermissions.Resolve(Permissions, Options).IsReadOnly;

    /// <summary>
    /// The exception group to show (<c>?exception=…</c>), so a group view can be linked. A value that
    /// isn't a current exception key is ignored and the page shows all failed jobs.
    /// </summary>
    [SupplyParameterFromQuery(Name = "exception")]
    public string? ExceptionQuery { get; set; }

    private JobList<FailedJobDto>? _jobs;
    private long _totalCount;
    private readonly ReversibleOrder _order = new();
    private int _from = 0;
    private int _pageSize = 20;
    private HashSet<string> _selectedJobs = new();
    private bool _selectAll;
    private DashboardPoller? _poller;

    // ── Exception groups ──
    // Loaded the first time the Exception filter gets focus, or when the page opens on a group, so
    // the default view costs no more than it did before grouping existed.
    private enum LoadStatus { NotLoaded, Loading, Loaded, TimedOut, Failed }

    private sealed record GroupRow(JobSummaryDto Summary, Job? Job);

    private IStorageQueryProvider? _queryProvider;
    private FailureGroupsResult _groups = new();
    private LoadStatus _groupsStatus = LoadStatus.NotLoaded;
    private CancellationTokenSource? _groupsCts;
    private bool _menuOpen;

    // The option the arrow keys have moved to, by fingerprint (null is "All exceptions"), so it stays on
    // the same group if a refresh reorders the list. Its index is the fallback when that group is gone.
    private string? _activeFingerprint;
    private int _activeIndex;
    private bool _scrollActiveOption;

    private string? _selectedFingerprint;
    private FailureGroupDto? _selectedGroup;
    private int _groupSize = 10;
    private List<GroupRow> _groupRows = new();
    private LoadStatus _groupJobsStatus = LoadStatus.NotLoaded;
    private CancellationTokenSource? _groupJobsCts;

    private bool ShowExceptionFilter => _queryProvider is { SupportsFailureGrouping: true } && _groups.IsSupported;
    private bool InGroupView => _selectedFingerprint is not null && ShowExceptionFilter;

    // ── Group actions: requeue or delete every job of the selected group ──
    // The group's ids are read first, and only those jobs are touched, in batches, each one only while
    // it is still failed. Jobs that fail into the group afterwards are left alone.
    private enum GroupActionKind { Requeue, Delete }

    private sealed record GroupAction(GroupActionKind Kind, string? Fingerprint, string Label,
        IReadOnlyList<string> JobIds, bool IsPartial);

    private GroupAction? _pendingGroupAction;
    private GroupAction? _runningAction;
    private BulkJobActionResult? _groupActionProgress;
    private bool _groupActionCancelRequested;
    private CancellationTokenSource? _groupActionCts;
    private CancellationTokenSource? _snapshotCts;
    private string? _groupActionMessage;
    private string? _groupActionMessageClass;
    private bool _disposed;

    private bool ShowGroupActions => InGroupView && !IsReadOnly;
    private bool GroupActionBusy => _snapshotCts is not null || _runningAction is not null;

    private IEnumerable<FailureGroupDto> LabeledGroups => _groups.Groups.Where(g => g.IsLabeled);
    private int UnlabeledGroupCount => _groups.Groups.Count(g => !g.IsLabeled);

    // The list's options: null for "All exceptions", then the labeled groups once they are loaded.
    private List<FailureGroupDto?> MenuOptions
    {
        get
        {
            var options = new List<FailureGroupDto?> { null };
            if (_groupsStatus == LoadStatus.Loaded)
                options.AddRange(LabeledGroups);
            return options;
        }
    }

    private static string OptionId(int index) => $"exception-option-{index}";

    private int ActiveOptionIn(List<FailureGroupDto?> options)
    {
        var index = options.FindIndex(g => g?.Fingerprint == _activeFingerprint);
        // The group has gone in a refresh: stay at the same position, inside the list.
        return index >= 0 ? index : Math.Clamp(_activeIndex, 0, options.Count - 1);
    }

    private bool HasMenuStatus => _groupsStatus is LoadStatus.Loading or LoadStatus.TimedOut or LoadStatus.Failed
        || (_groupsStatus == LoadStatus.Loaded && UnlabeledGroupCount > 0);

    private string? MenuStatusClass => !HasMenuStatus ? null : "px-3 py-2 border-top " + _groupsStatus switch
    {
        LoadStatus.TimedOut => "text-warning-emphasis",
        LoadStatus.Failed => "text-danger",
        _ => "text-muted",
    };

    private string SelectedLabel => !InGroupView
        ? "All exceptions"
        : _selectedGroup is { IsLabeled: true } ? ShortTypeName(_selectedGroup) : "Exception";

    private string TimedOutMessage => Options.SearchTimeoutSeconds > 0
        ? $"Grouping by exception took longer than {Options.SearchTimeoutSeconds} {(Options.SearchTimeoutSeconds == 1 ? "second" : "seconds")} and was stopped."
        : "Grouping by exception was stopped.";

    protected override async Task OnInitializedAsync()
    {
        _queryProvider = Services.GetService<IStorageQueryProvider>();
        await LoadData();

        _poller = Polling.Start(nameof(Failed), Polling.Options.PageRefreshInterval, Refresh);
    }

    private async Task Refresh(CancellationToken ct)
    {
        var (count, jobs) = await Polling.ReadAsync(() =>
        {
            var total = Monitor.GetFailedCount();
            return (total, total != _totalCount ? FetchPage(total) : null);
        }, ct);
        if (jobs is null) return;

        await InvokeAsync(async () =>
        {
            _totalCount = count;
            _jobs = jobs;
            StateHasChanged();

            // A running group action changes the count with every batch; the groups are
            // refreshed once it ends instead.
            if (_runningAction is null)
                await RefreshGroups();
        });
    }

    protected override async Task OnParametersSetAsync()
    {
        var requested = FailureFingerprint.IsCurrentVersion(ExceptionQuery) ? ExceptionQuery : null;
        if (requested == _selectedFingerprint)
            return;

        if (requested is null || _queryProvider is not { SupportsFailureGrouping: true })
        {
            ClearSelection();
            return;
        }

        _selectedFingerprint = requested;
        await LoadGroupsAndSelection();
    }

    private async Task LoadData()
    {
        await Polling.ReadAsync(() =>
        {
            _totalCount = Monitor.GetFailedCount();
            _jobs = FetchPage(_totalCount);
        });
    }

    private JobList<FailedJobDto>? FetchPage(long total) =>
        _order.Fetch(Monitor.GetFailedJobs, total, _from, _pageSize, j => j.FailedAt);

    // Grouping reads up to 1,000 failed jobs, so it is only redone while someone is looking at it.
    // Otherwise the groups are dropped and read again the next time the filter gets focus.
    private async Task RefreshGroups()
    {
        if (InGroupView || _menuOpen)
            await LoadGroupsAndSelection();
        else
            MarkGroupsStale();
    }

    private async Task EnsureGroupsLoaded()
    {
        if (_groupsStatus == LoadStatus.NotLoaded)
            await LoadGroupsAndSelection();
    }

    private void MarkGroupsStale()
    {
        if (_groupsStatus == LoadStatus.NotLoaded)
            return;
        // Dropping the source makes a load still in flight discard its result.
        _groupsCts?.Cancel();
        _groupsCts = null;
        _groupsStatus = LoadStatus.NotLoaded;
    }

    private async Task ToggleMenu()
    {
        if (_menuOpen)
            CloseMenu();
        else
            await OpenMenu();
    }

    private async Task OpenMenu()
    {
        _menuOpen = true;
        _activeFingerprint = _selectedFingerprint;
        _activeIndex = 0;
        _scrollActiveOption = true;
        await EnsureGroupsLoaded();
    }

    private void CloseMenu() => _menuOpen = false;

    // The select-only combobox keys (WAI-ARIA APG). nav.js cancels the browser's own handling of the
    // keys used here, apart from Escape and Tab: Tab leaves the combobox, and focusout closes the list.
    private async Task HandleFilterKeyDown(KeyboardEventArgs e)
    {
        if (e.AltKey || e.CtrlKey || e.MetaKey)
            return;

        if (!_menuOpen)
        {
            if (e.Key is "ArrowDown" or "ArrowUp" or "Enter" or " " or "Home" or "End")
            {
                await OpenMenu();
                // After OpenMenu, which loads the groups the first time, so End can reach the last one.
                if (e.Key is "Home" or "End")
                {
                    var loaded = MenuOptions;
                    MoveActiveOption(loaded, e.Key == "Home" ? 0 : loaded.Count - 1);
                }
            }
            return;
        }

        var options = MenuOptions;
        var active = ActiveOptionIn(options);
        var last = options.Count - 1;
        switch (e.Key)
        {
            case "ArrowDown":
                MoveActiveOption(options, Math.Min(active + 1, last));
                break;
            case "ArrowUp":
                MoveActiveOption(options, Math.Max(active - 1, 0));
                break;
            case "Home":
                MoveActiveOption(options, 0);
                break;
            case "End":
                MoveActiveOption(options, last);
                break;
            case "Enter":
            case " ":
                await SelectGroup(options[active]?.Fingerprint);
                break;
            case "Escape":
                CloseMenu();
                break;
        }
    }

    private void MoveActiveOption(List<FailureGroupDto?> options, int index)
    {
        _activeFingerprint = options[index]?.Fingerprint;
        _activeIndex = index;
        _scrollActiveOption = true;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_scrollActiveOption || !_menuOpen)
            return;
        _scrollActiveOption = false;
        try
        {
            await JS.InvokeVoidAsync("hfListbox.scrollIntoView", OptionId(ActiveOptionIn(MenuOptions)));
        }
        catch (Exception)
        {
            // Only keeps the option in view; the list still works without it.
        }
    }

    private async Task SelectGroup(string? fingerprint)
    {
        _menuOpen = false;
        if (fingerprint == _selectedFingerprint)
            return;

        if (fingerprint is null)
        {
            ClearSelection();
            Navigation.NavigateTo("jobs/failed", replace: true);
            return;
        }

        _selectedFingerprint = fingerprint;
        _selectedGroup = FindGroup(fingerprint);
        _groupRows = new();
        ClearJobSelection();
        // Set before navigating, so the parameter update the navigation causes finds nothing to do.
        // The key is a current fingerprint (prefix and hex digits only), so it needs no escaping.
        Navigation.NavigateTo($"jobs/failed?exception={fingerprint}", replace: true);
        await LoadGroupJobs();
    }

    private void ClearSelection()
    {
        // Dropping the source as well makes a load still in flight discard its result.
        _groupJobsCts?.Cancel();
        _groupJobsCts = null;
        _selectedFingerprint = null;
        _selectedGroup = null;
        _groupRows = new();
        _groupJobsStatus = LoadStatus.NotLoaded;
        ClearJobSelection();
    }

    private async Task LoadGroupsAndSelection()
    {
        await LoadGroups();
        if (_selectedFingerprint is not null)
        {
            _selectedGroup = FindGroup(_selectedFingerprint);
            await LoadGroupJobs();
        }
    }

    private async Task LoadGroups()
    {
        if (_queryProvider is not { SupportsFailureGrouping: true })
            return;

        _groupsCts?.Cancel();
        using var cts = NewTimeoutSource();
        _groupsCts = cts;

        // A refresh keeps the current list on screen until the new one arrives.
        if (_groupsStatus != LoadStatus.Loaded)
        {
            _groupsStatus = LoadStatus.Loading;
            StateHasChanged();
        }

        FailureGroupsResult? result = null;
        var status = LoadStatus.Loaded;
        try
        {
            // Off the render thread: a provider may do its work before its first await.
            result = await Task.Run(() => _queryProvider.GetFailureGroupsAsync(FailureGroupsResult.DefaultLabelCount, cts.Token));
        }
        catch (Exception)
        {
            // SqlClient reports a cancelled command as a SqlException, so the token tells a timeout
            // apart from a real error.
            status = cts.IsCancellationRequested ? LoadStatus.TimedOut : LoadStatus.Failed;
        }

        // A newer load, or disposal, has taken over.
        if (!ReferenceEquals(_groupsCts, cts))
            return;
        _groupsCts = null;

        if (status == LoadStatus.Loaded)
            _groups = result ?? FailureGroupsResult.Unsupported();
        _groupsStatus = status;
        if (!_groups.IsSupported)
            ClearSelection();
        StateHasChanged();
    }

    private async Task LoadGroupJobs()
    {
        _groupJobsCts?.Cancel();
        var group = _selectedGroup;
        if (group is null || !ShowExceptionFilter)
        {
            _groupRows = new();
            _groupJobsStatus = LoadStatus.Loaded;
            return;
        }

        using var cts = NewTimeoutSource();
        _groupJobsCts = cts;
        _groupJobsStatus = LoadStatus.Loading;
        StateHasChanged();

        List<GroupRow>? rows = null;
        var status = LoadStatus.Loaded;
        try
        {
            var size = _groupSize;
            rows = await Task.Run(async () =>
            {
                var jobs = await _queryProvider!.GetFailureGroupJobsAsync(group, size, cts.Token);
                return (jobs ?? Array.Empty<JobSummaryDto>())
                    .Select(j => new GroupRow(j, TryLoadJob(j.InvocationData)))
                    .ToList();
            });
        }
        catch (Exception)
        {
            status = cts.IsCancellationRequested ? LoadStatus.TimedOut : LoadStatus.Failed;
        }

        if (!ReferenceEquals(_groupJobsCts, cts))
            return;
        _groupJobsCts = null;

        _groupRows = rows ?? new();
        _groupJobsStatus = status;
        StateHasChanged();
    }

    private CancellationTokenSource NewTimeoutSource()
    {
        var cts = new CancellationTokenSource();
        if (Options.SearchTimeoutSeconds > 0)
            cts.CancelAfter(TimeSpan.FromSeconds(Options.SearchTimeoutSeconds));
        return cts;
    }

    private FailureGroupDto? FindGroup(string fingerprint)
        => _groups.Groups.FirstOrDefault(g => string.Equals(g.Fingerprint, fingerprint, StringComparison.Ordinal));

    // The job as the other lists show it (display name attributes, arguments); null when the dashboard
    // can't load the job's type, in which case the invocation data is shown instead.
    private static Job? TryLoadJob(InvocationData? invocationData)
    {
        if (invocationData is null)
            return null;
        try
        {
            return invocationData.DeserializeJob();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ── Labels: the same as the Exception filter on Search (FailureGroupLabels) ──

    private static string ShortTypeName(FailureGroupDto group) => FailureGroupLabels.ShortTypeName(group);

    private static string FullTypeName(FailureGroupDto group) => FailureGroupLabels.FullTypeName(group);

    private string CountText(FailureGroupDto group) => FailureGroupLabels.CountText(group, _groups.IsPartial);

    private static string Truncate(string text, int length) => FailureGroupLabels.Truncate(text, length);

    private async Task ToggleOrder()
    {
        _order.Toggle();
        _from = 0;
        ClearJobSelection();
        await LoadData();
    }

    private async Task HandlePageChanged((int From, int PageSize) args)
    {
        _from = args.From;
        _pageSize = args.PageSize;
        ClearJobSelection();
        await LoadData();
    }

    private async Task HandlePageSizeChanged(int newSize)
    {
        // The group view has no pager: its size is how many of the group's newest jobs are shown.
        if (InGroupView)
        {
            _groupSize = newSize;
            ClearJobSelection();
            await LoadGroupJobs();
            return;
        }

        _pageSize = newSize;
        _from = 0;
        ClearJobSelection();
        await LoadData();
    }

    // The rows the checkboxes refer to: the group's jobs in the group view, the page otherwise.
    private List<string> VisibleJobIds => InGroupView
        ? _groupRows.Select(r => r.Summary.JobId).ToList()
        : _jobs?.Select(j => j.Key).ToList() ?? new List<string>();

    private bool IsSelected(string jobId) => _selectedJobs.Contains(jobId);

    private void ToggleJob(string jobId)
    {
        if (!_selectedJobs.Remove(jobId)) _selectedJobs.Add(jobId);
        var visible = VisibleJobIds;
        _selectAll = visible.Count > 0 && visible.All(_selectedJobs.Contains);
    }

    private void ToggleAll(ChangeEventArgs e)
    {
        if ((bool)(e.Value ?? false))
        {
            _selectedJobs = new HashSet<string>(VisibleJobIds);
            _selectAll = _selectedJobs.Count > 0;
        }
        else
        {
            ClearJobSelection();
        }
    }

    private void ClearJobSelection()
    {
        _selectedJobs.Clear();
        _selectAll = false;
    }

    // Only jobs still in Failed are changed: a selected job that was retried or deleted elsewhere in
    // the meantime is left as it is.
    private async Task RequeueSelected()
    {
        var ids = _selectedJobs.ToList();
        await Task.Run(() => Monitor.RequeueJobs(ids, FailedState.StateName));
        await AfterJobsChanged();
    }

    private async Task DeleteSelected()
    {
        var ids = _selectedJobs.ToList();
        await Task.Run(() => Monitor.DeleteJobs(ids, FailedState.StateName));
        await AfterJobsChanged();
    }

    private async Task AfterJobsChanged()
    {
        ClearJobSelection();
        await LoadData();
        // LoadData takes the new failed count, so the refresh timer won't see the change.
        await RefreshGroups();
    }

    // ── Group actions ──

    private async Task PrepareGroupAction(GroupActionKind kind)
    {
        var group = _selectedGroup;
        if (group is null || GroupActionBusy || _queryProvider is null)
            return;

        _groupActionMessage = null;
        var label = SelectedLabel;
        var isPartial = _groups.IsPartial;
        var cts = NewTimeoutSource();
        _snapshotCts = cts;

        IReadOnlyList<string>? ids = null;
        string? error = null;
        try
        {
            ids = await Task.Run(() => _queryProvider.GetFailureGroupJobIdsAsync(group, cts.Token));
        }
        catch (Exception)
        {
            error = cts.IsCancellationRequested && !_disposed
                ? $"Reading the jobs with this exception took longer than {Options.SearchTimeoutSeconds} {(Options.SearchTimeoutSeconds == 1 ? "second" : "seconds")} and was stopped. No jobs were changed."
                : "The jobs with this exception could not be read. No jobs were changed.";
        }
        finally
        {
            _snapshotCts = null;
            cts.Dispose();
        }

        if (_disposed)
            return;
        if (error is not null)
            ShowGroupActionMessage(error, "alert-danger");
        else if (ids is not { Count: > 0 })
            ShowGroupActionMessage("No jobs with this exception are still failed.", "alert-secondary");
        else
            _pendingGroupAction = new GroupAction(kind, group.Fingerprint, label, ids, isPartial);
    }

    private static string GroupActionConfirmMessage(GroupAction action)
    {
        var verb = action.Kind == GroupActionKind.Requeue ? "Requeue" : "Delete";
        var count = action.JobIds.Count;
        var message = $"{verb} {count:N0} failed {(count == 1 ? "job" : "jobs")} with {action.Label}?";
        // The jobs the grouping did not reach can't be in the snapshot.
        if (action.IsPartial)
            message += " Older failures that were not grouped are not included.";
        return message + " Jobs that fail with this exception from now on are not included.";
    }

    private async Task RunGroupAction()
    {
        var action = _pendingGroupAction;
        _pendingGroupAction = null;
        if (action is null || _runningAction is not null)
            return;

        var cts = new CancellationTokenSource();
        _groupActionCts = cts;
        _groupActionCancelRequested = false;
        _runningAction = action;
        _groupActionMessage = null;
        _groupActionProgress = new BulkJobActionResult(action.JobIds.Count, 0, 0, Cancelled: false);
        StateHasChanged();

        var metadata = new Dictionary<string, string> { ["fingerprint"] = action.Fingerprint! };
        var progress = new ProgressCallback(p => _ = InvokeAsync(() =>
        {
            if (_disposed || _runningAction != action)
                return;
            _groupActionProgress = p;
            StateHasChanged();
        }));

        BulkJobActionResult? result = null;
        try
        {
            result = action.Kind == GroupActionKind.Requeue
                ? await Monitor.RequeueJobsInBatchesAsync(action.JobIds, FailedState.StateName, metadata, progress, cts.Token)
                : await Monitor.DeleteJobsInBatchesAsync(action.JobIds, FailedState.StateName, metadata, progress, cts.Token);
        }
        catch (Exception)
        {
            // A failed batch comes back in the result with exact counts; this is anything else.
        }

        _groupActionCts = null;
        _runningAction = null;
        cts.Dispose();
        if (_disposed)
            return;

        if (result is null || result.Error is not null)
            ShowGroupActionMessage(GroupActionErrorMessage(action, result), "alert-danger");
        else
            ShowGroupActionMessage(GroupActionSummary(action, result), result.Cancelled ? "alert-warning" : "alert-success");
        _groupActionProgress = null;
        StateHasChanged();

        await LoadData();
        await RefreshGroups();
    }

    private void CancelGroupAction()
    {
        _groupActionCancelRequested = true;
        _groupActionCts?.Cancel();
    }

    private void ShowGroupActionMessage(string message, string cssClass)
    {
        _groupActionMessage = message;
        _groupActionMessageClass = cssClass;
    }

    private static string GroupActionSummary(GroupAction action, BulkJobActionResult result)
    {
        var done = action.Kind == GroupActionKind.Requeue ? "Requeued" : "Deleted";
        var summary = result.Cancelled
            ? $"Cancelled after {result.Processed:N0} of {result.Total:N0} jobs with {action.Label}: {done.ToLowerInvariant()} {result.Succeeded:N0}."
            : $"{done} {result.Succeeded:N0} of {result.Total:N0} jobs with {action.Label}.";
        return summary + SkippedText(result);
    }

    // The result of a failed batch counts the jobs it changed before the error, so the numbers are
    // exact. Without a result (not expected), nothing is known beyond the audit log.
    private static string GroupActionErrorMessage(GroupAction action, BulkJobActionResult? result)
    {
        var verb = action.Kind == GroupActionKind.Requeue ? "Requeue all" : "Delete all";
        if (result is null)
            return $"{verb} stopped with an error. The audit log lists the batches that ran.";
        var done = action.Kind == GroupActionKind.Requeue ? "requeued" : "deleted";
        return $"{verb} stopped with an error: {done} {result.Succeeded:N0} of {result.Total:N0} jobs with {action.Label}."
            + SkippedText(result);
    }

    private static string SkippedText(BulkJobActionResult result) => result.Skipped > 0
        ? $" {result.Skipped:N0} {(result.Skipped == 1 ? "was" : "were")} skipped because {(result.Skipped == 1 ? "it" : "they")} had left Failed."
        : "";

    private static string ProgressText(GroupAction action, BulkJobActionResult progress)
        => $"{(action.Kind == GroupActionKind.Requeue ? "Requeued" : "Deleted")} {progress.Processed:N0} of {progress.Total:N0}";

    private static int ProgressPercent(BulkJobActionResult progress)
        => progress.Total == 0 ? 0 : (int)(100L * progress.Processed / progress.Total);

    // Reports straight to the callback, on the thread that finished the batch; the callback moves to
    // the renderer itself.
    private sealed class ProgressCallback : IProgress<BulkJobActionResult>
    {
        private readonly Action<BulkJobActionResult> _report;

        public ProgressCallback(Action<BulkJobActionResult> report) => _report = report;

        public void Report(BulkJobActionResult value) => _report(value);
    }

    private bool _showDeleteConfirm;
    private void ShowDeleteConfirm() => _showDeleteConfirm = true;
    private void CancelDelete() => _showDeleteConfirm = false;
    private async Task ConfirmDelete()
    {
        _showDeleteConfirm = false;
        await DeleteSelected();
    }

    private static string ShortenId(string? id)
    {
        if (Guid.TryParse(id, out _))
            return id[..8] + "…";
        return $"#{id}";
    }


    private static string? TruncateReason(string reason)
    {
        if (reason is null) return null;
        return reason.Length > 80 ? reason[..80] + "…" : reason;
    }

    public void Dispose()
    {
        _disposed = true;
        _poller?.Dispose();
        _groupsCts?.Cancel();
        _groupJobsCts?.Cancel();
        _snapshotCts?.Cancel();
        // Leaving the page stops a group action after the batch in progress.
        _groupActionCts?.Cancel();
    }
}
