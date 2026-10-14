using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.States;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Services;
using AwaitingPage = a2n.Hangfire.Dashboard.Components.Pages.Jobs.Awaiting;
using DeletedPage = a2n.Hangfire.Dashboard.Components.Pages.Jobs.Deleted;
using DetailsPage = a2n.Hangfire.Dashboard.Components.Pages.Jobs.Details;
using EnqueuedPage = a2n.Hangfire.Dashboard.Components.Pages.Jobs.Enqueued;
using FailedPage = a2n.Hangfire.Dashboard.Components.Pages.Jobs.Failed;
using ProcessingPage = a2n.Hangfire.Dashboard.Components.Pages.Jobs.Processing;
using RecurringPage = a2n.Hangfire.Dashboard.Components.Pages.Recurring;
using RetriesPage = a2n.Hangfire.Dashboard.Components.Pages.Retries;
using ScheduledPage = a2n.Hangfire.Dashboard.Components.Pages.Jobs.Scheduled;
using SucceededPage = a2n.Hangfire.Dashboard.Components.Pages.Jobs.Succeeded;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// <see cref="DashboardUIOptions.IsReadOnly"/> blocks every action that changes jobs, recurring jobs
/// or queues, in the services and on the pages (issue #73). Before, only the Job Builder and the
/// Failed page's group actions honored it.
/// </summary>
public sealed class ReadOnlyModeTests : IDisposable
{
    private static readonly Job SampleJob = Job.FromExpression(() => Console.WriteLine("read-only"));

    private readonly InMemoryStorage _storage = new();
    private readonly BackgroundJobClient _client;
    private readonly DashboardUIOptions _options = new() { EnableJobManagement = true };
    private readonly HangfireMonitorService _monitor;
    private readonly BunitContext _ctx = new();

    public ReadOnlyModeTests()
    {
        // No global filters: AutomaticRetry would turn the seeded Failed jobs into Scheduled ones.
        _client = new BackgroundJobClient(_storage, new JobFilterCollection());
        _monitor = new HangfireMonitorService(_storage, null, _options, new JobMethodResolver());

        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddSingleton(_options);
        _ctx.Services.AddDashboardPolling();
        _ctx.Services.AddSingleton(_monitor);
        _ctx.Services.AddSingleton(new ConsoleDataReader(_storage));
        _ctx.Services.AddSingleton(new TagsDataReader(_storage));
        _ctx.Services.AddSingleton(new JobGraphService(_monitor));
    }

    public void Dispose() => _ctx.Dispose();

    private string Create(IState state) => _client.Create(SampleJob, state);

    private string StateOf(string jobId)
    {
        using var connection = _storage.GetConnection();
        return connection.GetStateData(jobId).Name;
    }

    private void AddToSet(string key, string value)
    {
        using var connection = _storage.GetConnection();
        using var tx = connection.CreateWriteTransaction();
        tx.AddToSet(key, value);
        tx.Commit();
    }

    private void SeedRecurring(string id)
    {
        var result = _monitor.CreateOrUpdateRecurringJob(new RecurringJobRequest(
            JobId: id,
            TypeName: typeof(RecurringEditorFixtureJob).FullName,
            MethodName: nameof(RecurringEditorFixtureJob.DoNothing),
            ParameterJson: "[]",
            Cron: "0 0 * * *",
            Queue: "default",
            TimeZoneId: null,
            IsCustomMethod: false));
        Assert.True(result.Success, result.Error);
    }

    private IReadOnlyList<string> RecurringIds()
        => _monitor.GetRecurringJobs().Select(j => j.Id).ToList();

    // ── Service layer ──

    public static TheoryData<string> JobMutations => new()
    {
        "RequeueJob", "DeleteJob", "RequeueJobs", "RequeueJobsFromState", "DeleteJobs", "DeleteJobsFromState",
    };

    [Theory]
    [MemberData(nameof(JobMutations))]
    public void JobMutations_AreRefused_AndChangeNothing(string mutation)
    {
        var id = Create(new FailedState(new InvalidOperationException("boom")));
        _options.IsReadOnly = true;

        Action act = mutation switch
        {
            "RequeueJob" => () => _monitor.RequeueJob(id),
            "DeleteJob" => () => _monitor.DeleteJob(id),
            "RequeueJobs" => () => _monitor.RequeueJobs(new[] { id }),
            "RequeueJobsFromState" => () => _monitor.RequeueJobs(new[] { id }, FailedState.StateName),
            "DeleteJobs" => () => _monitor.DeleteJobs(new[] { id }),
            "DeleteJobsFromState" => () => _monitor.DeleteJobs(new[] { id }, FailedState.StateName),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        var ex = Assert.Throws<InvalidOperationException>(act);
        Assert.Contains("read-only", ex.Message);
        Assert.Equal(FailedState.StateName, StateOf(id));
    }

    [Fact]
    public async Task BatchedMutations_AreRefused_BeforeAnyBatchRuns()
    {
        var id = Create(new FailedState(new InvalidOperationException("boom")));
        _options.IsReadOnly = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _monitor.RequeueJobsInBatchesAsync(new[] { id }, FailedState.StateName));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _monitor.DeleteJobsInBatchesAsync(new[] { id }, FailedState.StateName));
        Assert.Equal(FailedState.StateName, StateOf(id));
    }

    [Fact]
    public void RecurringMutations_AreRefused_AndChangeNothing()
    {
        SeedRecurring("nightly");
        SeedRecurring("stopped");
        _monitor.StopRecurringJob("stopped");
        _options.IsReadOnly = true;

        Assert.Throws<InvalidOperationException>(() => _monitor.TriggerRecurringJob("nightly"));
        Assert.Throws<InvalidOperationException>(() => _monitor.TriggerRecurringJobs(new[] { "nightly" }));
        Assert.Throws<InvalidOperationException>(() => _monitor.RemoveRecurringJob("nightly"));
        Assert.Throws<InvalidOperationException>(() => _monitor.RemoveRecurringJobs(new[] { "nightly" }));
        Assert.Throws<InvalidOperationException>(() => _monitor.StopRecurringJob("nightly"));
        Assert.Throws<InvalidOperationException>(() => _monitor.StartRecurringJob("stopped"));

        Assert.Equal(new[] { "nightly" }, RecurringIds());
        Assert.Contains("stopped", _monitor.GetStoppedRecurringJobIds());
        // Not triggered: no job was created for it.
        Assert.Null(_monitor.GetRecurringJobs().Single().LastJobId);
    }

    [Fact]
    public void JobBuilderMutations_StillReportReadOnly_InTheirResult()
    {
        _options.IsReadOnly = true;

        var result = _monitor.EnqueueJob(new EnqueueJobRequest(
            TypeName: typeof(RecurringEditorFixtureJob).FullName,
            MethodName: nameof(RecurringEditorFixtureJob.DoNothing),
            ParameterJson: "[]",
            Queue: "default",
            IsCustomMethod: false));

        Assert.False(result.Success);
        Assert.Equal(HangfireMonitorService.ReadOnlyMessage, result.Error);
    }

    [Fact]
    public void QueueOperations_AreRefused()
    {
        var audit = new AuditLogService(_storage, _options, httpContext: null);
        var operations = new QueueOperationsService(_storage, _options, audit);
        _options.IsReadOnly = true;

        Assert.Throws<InvalidOperationException>(() => operations.PauseQueue("default"));
        Assert.Throws<InvalidOperationException>(() => operations.ResumeQueue("default"));
        Assert.Throws<InvalidOperationException>(() => operations.EnableMaintenanceMode());
        Assert.Throws<InvalidOperationException>(() => operations.DisableMaintenanceMode());

        var state = operations.GetState();
        Assert.Empty(state.PausedQueues);
        Assert.False(state.MaintenanceMode);
    }

    // ── Pages ──
    // Each page is rendered with data in both modes: the actions must be there when writable (so the
    // read-only check isn't passing on an empty page) and gone when read-only.

    private static readonly string[] ActionLabels =
        { "Requeue", "Delete", "Enqueue", "Trigger", "Requeue all", "Delete all", "Start", "Create Job", "Enqueue Job" };

    private static bool HasRows<TPage>(IRenderedComponent<TPage> cut) where TPage : IComponent
        => cut.FindAll("tbody tr").Count > 0;

    private void AssertActions<TPage>(bool readOnly, Func<IRenderedComponent<TPage>, bool> ready,
        Action<ComponentParameterCollectionBuilder<TPage>> parameters = null, bool hasCheckboxes = true)
        where TPage : IComponent
    {
        _options.IsReadOnly = readOnly;
        var cut = parameters is null ? _ctx.Render<TPage>() : _ctx.Render<TPage>(parameters);
        cut.WaitForState(() => ready(cut), TestTimeouts.RenderWait);

        // Sort buttons in the column headers aren't actions (Scheduled has an "Enqueue" column).
        var buttons = cut.FindAll("button, a.btn")
            .Where(b => b.Closest("th") is null)
            .Select(b => b.TextContent.Trim())
            .Where(text => ActionLabels.Contains(text))
            .ToList();
        var checkboxes = cut.FindAll("input[type=checkbox]").Count;

        if (readOnly)
        {
            Assert.Empty(buttons);
            Assert.Equal(0, checkboxes);
        }
        else
        {
            Assert.NotEmpty(buttons);
            if (hasCheckboxes)
                Assert.True(checkboxes > 0, "Expected row checkboxes when the dashboard is writable.");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SucceededPage_ActionsFollowReadOnly(bool readOnly)
    {
        Create(new SucceededState(null, 0, 0));
        AssertActions<SucceededPage>(readOnly, HasRows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedPage_ActionsFollowReadOnly(bool readOnly)
    {
        Create(new FailedState(new InvalidOperationException("boom")));
        AssertActions<FailedPage>(readOnly, HasRows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScheduledPage_ActionsFollowReadOnly(bool readOnly)
    {
        Create(new ScheduledState(TimeSpan.FromHours(1)));
        AssertActions<ScheduledPage>(readOnly, HasRows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnqueuedPage_ActionsFollowReadOnly(bool readOnly)
    {
        Create(new EnqueuedState("default"));
        AssertActions<EnqueuedPage>(readOnly, HasRows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeletedPage_ActionsFollowReadOnly(bool readOnly)
    {
        Create(new DeletedState());
        AssertActions<DeletedPage>(readOnly, HasRows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AwaitingPage_ActionsFollowReadOnly(bool readOnly)
    {
        var parent = Create(new ScheduledState(TimeSpan.FromHours(1)));
        var child = Create(new AwaitingState(parent));
        AddToSet("awaiting", child);
        AssertActions<AwaitingPage>(readOnly, HasRows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetriesPage_ActionsFollowReadOnly(bool readOnly)
    {
        var id = Create(new ScheduledState(TimeSpan.FromHours(1)));
        AddToSet("retries", id);
        AssertActions<RetriesPage>(readOnly, HasRows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProcessingPage_ActionsFollowReadOnly(bool readOnly)
    {
        Create(new TestProcessingState());
        AssertActions<ProcessingPage>(readOnly, HasRows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DetailsPage_ActionsFollowReadOnly(bool readOnly)
    {
        var id = Create(new FailedState(new InvalidOperationException("boom")));
        AssertActions<DetailsPage>(readOnly, cut => cut.Markup.Contains("State History"),
            p => p.Add(c => c.JobId, id), hasCheckboxes: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecurringPage_ActionsFollowReadOnly(bool readOnly)
    {
        SeedRecurring("nightly");
        SeedRecurring("stopped");
        _monitor.StopRecurringJob("stopped");
        AssertActions<RecurringPage>(readOnly, cut => cut.Markup.Contains("Stopped Jobs") && HasRows(cut));
    }

    [Fact]
    public void RecurringPage_ReadOnly_HidesEditAndStop()
    {
        SeedRecurring("nightly");
        _options.IsReadOnly = true;

        var cut = _ctx.Render<RecurringPage>();
        cut.WaitForState(() => HasRows(cut), TestTimeouts.RenderWait);

        Assert.Empty(cut.FindAll("a[href^='recurring/edit/']"));
        Assert.Empty(cut.FindAll("button[title='Stop this recurring job']"));
        Assert.Empty(cut.FindAll("th.hf-cell-actions"));
    }

    /// <summary>Processing, as <c>ProcessingState</c> serializes it (its constructor is internal).</summary>
    private sealed class TestProcessingState : IState
    {
        public string Name => ProcessingState.StateName;
        public string Reason => null;
        public bool IsFinal => false;
        public bool IgnoreJobLoadException => false;

        public Dictionary<string, string> SerializeData() => new()
        {
            ["StartedAt"] = JobHelper.SerializeDateTime(DateTime.UtcNow),
            ["ServerId"] = "test-server",
            ["WorkerId"] = "1",
        };
    }
}
