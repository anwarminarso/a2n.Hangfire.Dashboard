using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services;
using FailedPage = a2n.Hangfire.Dashboard.Components.Pages.Jobs.Failed;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Requeue all / Delete all for an exception group on the Failed page (#53), and row selection in
/// the group view. The jobs are real Hangfire.InMemory jobs; the provider is mocked to return them as
/// one group, the way the storage adapters would.
/// </summary>
public class FailedPageGroupActionsTests : IDisposable
{
    private const string GroupKey = "v1:0123456789abcdef";
    private const string GroupLabel = "SaveChangesFailedException → SqlException";

    private static readonly Job OrderJob = Job.FromExpression(() => FailureGroupingTests.OrderJobs.Run());

    private readonly BunitContext _ctx = new();
    private readonly GatedStorage _storage = new(new InMemoryStorage());
    private readonly BackgroundJobClient _client;
    private readonly AuditLogService _audit;
    private readonly DashboardUIOptions _options = new();
    private readonly Mock<IStorageQueryProvider> _provider = new();

    // The group's members as the provider reports them, newest first.
    private List<string> _members = new();

    public FailedPageGroupActionsTests()
    {
        _client = new BackgroundJobClient(_storage, new JobFilterCollection());
        // On the inner storage, so the audit log's own writes (and its occasional trim) never pass
        // the gate the state changes wait at.
        _audit = new AuditLogService(_storage.Inner, _options, httpContext: null);

        _provider.SetupGet(p => p.SupportsFailureGrouping).Returns(true);
        _provider.Setup(p => p.GetFailureGroupsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new FailureGroupsResult
            {
                UsesStoredFingerprints = true,
                Groups = new[]
                {
                    new FailureGroupDto
                    {
                        Fingerprint = GroupKey, Count = StillFailed(_members).Count, StoredCount = StillFailed(_members).Count, IsLabeled = true,
                        ExceptionType = "Contoso.Data.SaveChangesFailedException",
                        InnerExceptionType = "Microsoft.Data.SqlClient.SqlException",
                        NormalizedMessage = "Cannot insert duplicate key", TopFrame = "Contoso.Jobs.OrderJob.Run",
                    },
                },
            });
        _provider.Setup(p => p.GetFailureGroupJobsAsync(It.IsAny<FailureGroupDto>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FailureGroupDto _, int count, CancellationToken _) => StillFailed(_members)
                .Take(count)
                .Select(id => new JobSummaryDto
                {
                    JobId = id,
                    JobName = "OrderJobs.Run",
                    State = "Failed",
                    LastStateChange = DateTime.UtcNow,
                    ExceptionType = "Contoso.Data.SaveChangesFailedException",
                    ExceptionMessage = "Cannot insert duplicate key",
                    InvocationData = InvocationData.SerializeJob(OrderJob),
                })
                .ToList());
        _provider.Setup(p => p.GetFailureGroupJobIdsAsync(It.IsAny<FailureGroupDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => StillFailed(_members));

        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddSingleton(_options);
        _ctx.Services.AddSingleton(new HangfireMonitorService(_storage, _audit, _options, new JobMethodResolver()));
        _ctx.Services.AddSingleton(_provider.Object);
    }

    public void Dispose()
    {
        // A test that fails while the gate is closed must not leave a batch waiting on it.
        _storage.Open();
        _ctx.Dispose();
    }

    private string AddFailedJob()
        => _client.Create(OrderJob, new FailedState(new InvalidOperationException("Order 7 is locked")));

    // Adds jobs to the group, newest first as the providers return them.
    private List<string> AddGroupJobs(int count)
    {
        var ids = Enumerable.Range(0, count).Select(_ => AddFailedJob()).ToList();
        _members = ids.AsEnumerable().Reverse().Concat(_members).ToList();
        return ids;
    }

    private List<string> StillFailed(IEnumerable<string> ids) => ids.Where(id => StateOf(id) == FailedState.StateName).ToList();

    private string StateOf(string jobId)
    {
        using var connection = _storage.GetReadOnlyConnection();
        return connection.GetStateData(jobId).Name;
    }

    private IReadOnlyList<AuditLogEntry> AuditEntries() => _audit.Query(new AuditLogFilter(), 0, 1000);

    // Selected through the filter: ClickAsync waits for the page's handler, so no render is still
    // pending that would replace the event handlers the test goes on to use.
    private async Task<IRenderedComponent<FailedPage>> RenderGroupView()
    {
        var cut = _ctx.Render<FailedPage>();
        cut.WaitForState(() => cut.FindAll("tbody tr").Count > 0, TestTimeouts.RenderWait);
        await cut.Find("#exception-filter").ClickAsync(new MouseEventArgs());
        cut.WaitForState(() => cut.FindAll(".hf-exception-option").Count > 0, TestTimeouts.RenderWait);
        await cut.Find(".hf-exception-option").ClickAsync(new MouseEventArgs());
        cut.WaitForState(() => cut.FindAll("[data-testid=group-total]").Count > 0 && GroupRowCount(cut) > 0, TestTimeouts.RenderWait);
        return cut;
    }

    private static int GroupRowCount(IRenderedComponent<FailedPage> cut)
        => cut.FindAll("tbody tr:not(.hf-row-detail)").Count;

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<FailedPage> cut, string text)
        => cut.FindAll("button").Single(b => b.TextContent.Trim() == text);

    private static AngleSharp.Dom.IElement ModalButton(IRenderedComponent<FailedPage> cut, string text)
        => cut.FindAll(".modal-footer button").Single(b => b.TextContent.Trim() == text);

    private static string Modal(IRenderedComponent<FailedPage> cut) => cut.Find(".modal-body").TextContent.Trim();

    private static async Task ClickAndWaitForModal(IRenderedComponent<FailedPage> cut, string testId)
    {
        await cut.Find($"[data-testid={testId}]").ClickAsync(new MouseEventArgs());
        cut.WaitForState(() => cut.FindAll(".modal-body").Count > 0, TestTimeouts.RenderWait);
    }

    private static void WaitForSummary(IRenderedComponent<FailedPage> cut)
        => cut.WaitForState(() => cut.FindAll("[data-testid=group-action-summary]").Count > 0, TestTimeouts.RenderWait);

    private static string Summary(IRenderedComponent<FailedPage> cut)
        => System.Text.RegularExpressions.Regex.Replace(cut.Find("[data-testid=group-action-summary]").TextContent, @"\s+", " ").Trim();

    [Fact]
    public void GroupActions_AreHiddenInTheAllExceptionsView()
    {
        AddGroupJobs(3);

        var cut = _ctx.Render<FailedPage>();
        cut.WaitForState(() => cut.FindAll("tbody tr").Count > 0, TestTimeouts.RenderWait);

        Assert.Empty(cut.FindAll("[data-testid=requeue-all]"));
        Assert.Empty(cut.FindAll("[data-testid=delete-all]"));
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Trim() == "Requeue");
    }

    [Fact]
    public async Task GroupActions_AreHiddenInReadOnlyMode()
    {
        _options.IsReadOnly = true;
        AddGroupJobs(3);

        var cut = await RenderGroupView();

        Assert.Empty(cut.FindAll("[data-testid=requeue-all]"));
        Assert.Empty(cut.FindAll("[data-testid=delete-all]"));
    }

    [Fact]
    public async Task RequeueAll_ConfirmsTheSnapshotCount_AndChangesOnlyTheSnapshot()
    {
        var ids = AddGroupJobs(5);
        var cut = await RenderGroupView();

        await ClickAndWaitForModal(cut, "requeue-all");

        // The ids are read when the button is clicked, before the confirmation.
        _provider.Verify(p => p.GetFailureGroupJobIdsAsync(It.Is<FailureGroupDto>(g => g.Fingerprint == GroupKey), It.IsAny<CancellationToken>()), Times.Once);
        Assert.StartsWith($"Requeue 5 failed jobs with {GroupLabel}?", Modal(cut));
        Assert.Contains("Confirm Requeue", cut.Find(".modal-title").TextContent);

        // While the dialog is open, one job succeeds on a retry and another fails into the group.
        _client.ChangeState(ids[0], new SucceededState(null, 0, 0));
        var failedLater = AddGroupJobs(1)[0];

        await ModalButton(cut, "Requeue all").ClickAsync(new MouseEventArgs());
        WaitForSummary(cut);

        Assert.Equal($"Requeued 4 of 5 jobs with {GroupLabel}. 1 was skipped because it had left Failed.", Summary(cut));
        Assert.All(ids.Skip(1), id => Assert.Equal(EnqueuedState.StateName, StateOf(id)));
        Assert.Equal(SucceededState.StateName, StateOf(ids[0]));
        Assert.Equal(FailedState.StateName, StateOf(failedLater));

        var entry = Assert.Single(AuditEntries());
        Assert.Equal(AuditAction.JobsRequeuedBatch, entry.Action);
        Assert.Equal(GroupKey, entry.Metadata["fingerprint"]);
        Assert.Equal("5", entry.Metadata["requested"]);
        Assert.Equal("4", entry.Metadata["succeeded"]);

        // The groups and the list are read again: only the job that failed later is left.
        cut.WaitForAssertion(() => Assert.Equal("1 failed job", cut.Find("[data-testid=group-total]").TextContent.Trim()), TestTimeouts.RenderWait);
        Assert.Equal(1, GroupRowCount(cut));
    }

    [Fact]
    public async Task DeleteAll_AsksFirst_AndCancellingTheDialogChangesNothing()
    {
        var ids = AddGroupJobs(3);
        var cut = await RenderGroupView();

        await ClickAndWaitForModal(cut, "delete-all");
        Assert.StartsWith($"Delete 3 failed jobs with {GroupLabel}?", Modal(cut));
        await ModalButton(cut, "Cancel").ClickAsync(new MouseEventArgs());

        Assert.Empty(cut.FindAll(".modal-body"));
        Assert.All(ids, id => Assert.Equal(FailedState.StateName, StateOf(id)));

        await ClickAndWaitForModal(cut, "delete-all");
        await ModalButton(cut, "Delete all").ClickAsync(new MouseEventArgs());
        WaitForSummary(cut);

        Assert.Equal($"Deleted 3 of 3 jobs with {GroupLabel}.", Summary(cut));
        Assert.All(ids, id => Assert.Equal(DeletedState.StateName, StateOf(id)));
        Assert.Equal(AuditAction.JobsDeletedBatch, Assert.Single(AuditEntries()).Action);
    }

    [Fact]
    public async Task PartialGroups_SayOlderFailuresAreNotIncluded()
    {
        AddGroupJobs(2);
        _provider.Setup(p => p.GetFailureGroupsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new FailureGroupsResult
            {
                UsesStoredFingerprints = true,
                UnfingerprintedCount = 1500,
                FallbackCount = JobFilterCriteria.CountLimit,
                Groups = new[]
                {
                    new FailureGroupDto
                    {
                        Fingerprint = GroupKey, Count = 2, ComputedCount = 2, IsLabeled = true,
                        ExceptionType = "System.TimeoutException", InnerExceptionType = "",
                    },
                },
            });
        var cut = await RenderGroupView();

        await ClickAndWaitForModal(cut, "requeue-all");

        Assert.StartsWith("Requeue 2 failed jobs with TimeoutException?", Modal(cut));
        Assert.Contains("Older failures that were not grouped are not included.", Modal(cut));
    }

    [Fact]
    public async Task Cancel_StopsBeforeTheNextBatch()
    {
        var ids = AddGroupJobs(1200);
        var cut = await RenderGroupView();
        await ClickAndWaitForModal(cut, "requeue-all");
        Assert.StartsWith($"Requeue {1200:N0} failed jobs", Modal(cut));

        // The first batch waits at the gate until Cancel has been clicked.
        _storage.Close();
        var run = ModalButton(cut, "Requeue all").ClickAsync(new MouseEventArgs());
        _storage.WaitUntilBlocked();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains($"Requeued 0 of {1200:N0}", cut.Find("[data-testid=group-action-progress]").TextContent);
            // Both group actions and the selection actions are disabled while it runs.
            Assert.True(cut.Find("[data-testid=requeue-all]").HasAttribute("disabled"));
            Assert.True(cut.Find("[data-testid=delete-all]").HasAttribute("disabled"));
            Assert.True(Button(cut, "Requeue").HasAttribute("disabled"));
        }, TestTimeouts.RenderWait);

        await Button(cut, "Cancel").ClickAsync(new MouseEventArgs());
        _storage.Open();
        await run;
        WaitForSummary(cut);

        Assert.Equal($"Cancelled after 500 of {1200:N0} jobs with {GroupLabel}: requeued 500.", Summary(cut));
        Assert.Equal(500, ids.Count(id => StateOf(id) == EnqueuedState.StateName));
        Assert.Equal(700, ids.Count(id => StateOf(id) == FailedState.StateName));
        Assert.Single(AuditEntries());
    }

    [Fact]
    public async Task AFailedBatch_IsReportedWithTheExactNumberOfJobsChanged()
    {
        var ids = AddGroupJobs(600);
        var cut = await RenderGroupView();
        await ClickAndWaitForModal(cut, "requeue-all");

        // The snapshot is newest first, so its 551st job is ids[49]: the second batch fails there,
        // after requeuing 50 jobs.
        _storage.Failure.FailJob(ids[49]);
        await ModalButton(cut, "Requeue all").ClickAsync(new MouseEventArgs());
        WaitForSummary(cut);

        Assert.Equal($"Requeue all stopped with an error: requeued 550 of 600 jobs with {GroupLabel}.", Summary(cut));
        Assert.Contains("alert-danger", cut.Find("[data-testid=group-action-summary]").ClassName);
        Assert.Equal(550, ids.Count(id => StateOf(id) == EnqueuedState.StateName));
        Assert.Equal(2, AuditEntries().Count);
    }

    [Fact]
    public async Task LeavingThePage_StopsBeforeTheNextBatch()
    {
        var ids = AddGroupJobs(1200);
        var cut = await RenderGroupView();
        await ClickAndWaitForModal(cut, "delete-all");

        _storage.Close();
        var run = ModalButton(cut, "Delete all").ClickAsync(new MouseEventArgs());
        _storage.WaitUntilBlocked();

        cut.Instance.Dispose();
        _storage.Open();
        await run;

        // The batch in progress finishes; nothing after it starts.
        Assert.Equal(500, ids.Count(id => StateOf(id) == DeletedState.StateName));
        Assert.Equal(700, ids.Count(id => StateOf(id) == FailedState.StateName));
        Assert.Single(AuditEntries());
    }

    [Fact]
    public async Task RowSelection_InTheGroupView_RequeuesTheSelectedJobs()
    {
        var ids = AddGroupJobs(4);
        var cut = await RenderGroupView();
        Assert.True(Button(cut, "Requeue").HasAttribute("disabled"));

        // Newest first: the rows are ids[3], ids[2], ids[1], ids[0].
        await cut.Find($"input[aria-label='Select job {ids[3]}']").ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.Find($"input[aria-label='Select job {ids[1]}']").ChangeAsync(new ChangeEventArgs { Value = true });
        Assert.Equal(2, cut.FindAll("tbody tr.table-active:not(.hf-row-detail)").Count);
        await Button(cut, "Requeue").ClickAsync(new MouseEventArgs());

        Assert.Equal(EnqueuedState.StateName, StateOf(ids[3]));
        Assert.Equal(EnqueuedState.StateName, StateOf(ids[1]));
        Assert.Equal(FailedState.StateName, StateOf(ids[2]));
        Assert.Equal(FailedState.StateName, StateOf(ids[0]));
        cut.WaitForAssertion(() => Assert.Equal(2, GroupRowCount(cut)), TestTimeouts.RenderWait);
        Assert.Empty(cut.FindAll("tbody input[type=checkbox]:checked"));

        // Select all selects the group's rows on screen.
        await cut.Find("thead input[aria-label='Select all']").ChangeAsync(new ChangeEventArgs { Value = true });
        Assert.Equal(2, cut.FindAll("tbody tr.table-active:not(.hf-row-detail)").Count);
        await Button(cut, "Delete").ClickAsync(new MouseEventArgs());
        Assert.Contains("delete 2 selected job(s)", Modal(cut));
    }

    [Fact]
    public async Task SelectionActions_OnlyChangeJobsStillInFailed()
    {
        var ids = AddGroupJobs(2);
        var cut = _ctx.Render<FailedPage>();
        cut.WaitForState(() => cut.FindAll("tbody input[type=checkbox]").Count == 2, TestTimeouts.RenderWait);

        await cut.Find("thead input[aria-label='Select all']").ChangeAsync(new ChangeEventArgs { Value = true });
        // Retried and succeeded elsewhere after the page was read.
        _client.ChangeState(ids[0], new SucceededState(null, 0, 0));
        await Button(cut, "Requeue").ClickAsync(new MouseEventArgs());

        Assert.Equal(SucceededState.StateName, StateOf(ids[0]));
        Assert.Equal(EnqueuedState.StateName, StateOf(ids[1]));
        var entry = Assert.Single(AuditEntries());
        Assert.Equal("2", entry.Metadata["requested"]);
        Assert.Equal("1", entry.Metadata["succeeded"]);
    }

    /// <summary>
    /// Hangfire.InMemory with a gate on <see cref="JobStorage.GetConnection"/>, which every state
    /// change opens, so a test can hold a batch in progress, and a <see cref="StateChangeFailure"/>
    /// to make one fail. Read-only connections, which the page and the test's own checks use, are
    /// never held.
    /// </summary>
    private sealed class GatedStorage : JobStorage
    {
        private readonly ManualResetEventSlim _gate = new(initialState: true);
        private readonly ManualResetEventSlim _blocked = new(initialState: false);

        public GatedStorage(JobStorage inner) => Inner = inner;

        public JobStorage Inner { get; }

        public StateChangeFailure Failure { get; } = new();

        public void Close()
        {
            _blocked.Reset();
            _gate.Reset();
        }

        public void Open() => _gate.Set();

        public void WaitUntilBlocked()
        {
            if (!_blocked.Wait(TestTimeouts.RenderWait))
                throw new TimeoutException("No state change reached the gate.");
        }

        public override IMonitoringApi GetMonitoringApi() => Inner.GetMonitoringApi();

        public override IStorageConnection GetConnection()
        {
            if (!_gate.IsSet)
            {
                _blocked.Set();
                _gate.Wait();
            }
            return Inner.GetConnection();
        }

        public override IStorageConnection GetReadOnlyConnection() => Inner.GetReadOnlyConnection();

        public override bool LinearizableReads => Inner.LinearizableReads;

        public override bool HasFeature(string featureId) => Inner.HasFeature(featureId);

        public override IEnumerable<IStateHandler> GetStateHandlers()
            => Inner.GetStateHandlers().Concat(Failure.HandlersFor());
    }
}
