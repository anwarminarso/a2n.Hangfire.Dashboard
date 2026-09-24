using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Helpers;
using a2n.Hangfire.Dashboard.Services;
using RecurringPage = a2n.Hangfire.Dashboard.Components.Pages.Recurring;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Column sorting on the Recurring Jobs grid
/// (<see href="https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/41">Issue #41</see>).
/// </summary>
public class RecurringSortingTests
{
    private static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    // ─── RecurringJobSorter ────────────────────────────────────────────────

    [Fact]
    public void None_KeepsStorageOrder()
    {
        var jobs = new[] { Dto("b"), Dto("a"), Dto("c") };

        var sorted = RecurringJobSorter.Sort(jobs, RecurringJobSortColumn.None, descending: false);

        Assert.Equal(new[] { "b", "a", "c" }, Ids(sorted));
    }

    [Fact]
    public void Id_SortsCaseInsensitively_InBothDirections()
    {
        var jobs = new[] { Dto("beta"), Dto("Alpha"), Dto("gamma") };

        Assert.Equal(new[] { "Alpha", "beta", "gamma" },
            Ids(RecurringJobSorter.Sort(jobs, RecurringJobSortColumn.Id, descending: false)));
        Assert.Equal(new[] { "gamma", "beta", "Alpha" },
            Ids(RecurringJobSorter.Sort(jobs, RecurringJobSortColumn.Id, descending: true)));
    }

    [Fact]
    public void Job_SortsByDisplayedName_GroupingSameJobType_TiesById()
    {
        var jobs = new[]
        {
            Dto("tenant-b", job: Job.FromExpression(() => SortFixtureJob.Sync())),
            Dto("tenant-a", job: Job.FromExpression(() => SortFixtureJob.Report())),
            Dto("tenant-c", job: Job.FromExpression(() => SortFixtureJob.Sync())),
            Dto("tenant-d", job: Job.FromExpression(() => SortFixtureJob.Report())),
        };

        var sorted = RecurringJobSorter.Sort(jobs, RecurringJobSortColumn.Job, descending: false);

        Assert.Equal(new[] { "tenant-a", "tenant-d", "tenant-b", "tenant-c" }, Ids(sorted));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NextExecution_MissingValuesGoLast_InBothDirections(bool descending)
    {
        var jobs = new[]
        {
            Dto("none", next: null),
            Dto("late", next: T0.AddHours(2)),
            Dto("early", next: T0.AddHours(1)),
        };

        var sorted = RecurringJobSorter.Sort(jobs, RecurringJobSortColumn.NextExecution, descending);

        var expected = descending ? new[] { "late", "early", "none" } : new[] { "early", "late", "none" };
        Assert.Equal(expected, Ids(sorted));
    }

    [Fact]
    public void LastExecution_NeverRunGoesLast()
    {
        var jobs = new[]
        {
            Dto("never", last: null),
            Dto("yesterday", last: T0.AddDays(-1)),
            Dto("today", last: T0),
        };

        var sorted = RecurringJobSorter.Sort(jobs, RecurringJobSortColumn.LastExecution, descending: true);

        Assert.Equal(new[] { "today", "yesterday", "never" }, Ids(sorted));
    }

    [Fact]
    public void Created_SortsByCreatedAt_UnresolvedJobDoesNotMatter()
    {
        var jobs = new[]
        {
            Dto("new", created: T0.AddDays(2), job: null),
            Dto("old", created: T0),
            Dto("unknown", created: null),
        };

        var sorted = RecurringJobSorter.Sort(jobs, RecurringJobSortColumn.Created, descending: false);

        Assert.Equal(new[] { "old", "new", "unknown" }, Ids(sorted));
    }

    [Fact]
    public void Job_UnresolvedJobGoesLast()
    {
        var jobs = new[]
        {
            Dto("unresolved", job: null),
            Dto("resolved", job: Job.FromExpression(() => SortFixtureJob.Sync())),
        };

        var sorted = RecurringJobSorter.Sort(jobs, RecurringJobSortColumn.Job, descending: true);

        Assert.Equal(new[] { "resolved", "unresolved" }, Ids(sorted));
    }

    // ─── Recurring page ────────────────────────────────────────────────────

    [Fact]
    public void ClickingHeader_SortsAscending_ThenDescending_AndMarksTheColumn()
    {
        var (ctx, svc) = NewContext();
        using var _ = ctx;
        Seed(svc, "beta", "alpha", "gamma");

        var cut = RenderPage(ctx);
        var idHeader = () => cut.FindAll("th").Single(th => th.TextContent.Trim() == "Id");

        Assert.Equal("none", idHeader().GetAttribute("aria-sort"));

        idHeader().QuerySelector("button")!.Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(new[] { "alpha", "beta", "gamma" }, RowIds(cut));
            Assert.Equal("ascending", idHeader().GetAttribute("aria-sort"));
        }, TestTimeouts.RenderWait);

        idHeader().QuerySelector("button")!.Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(new[] { "gamma", "beta", "alpha" }, RowIds(cut));
            Assert.Equal("descending", idHeader().GetAttribute("aria-sort"));
        }, TestTimeouts.RenderWait);
    }

    [Fact]
    public void Sort_AppliesToTheWholeList_AndIsKeptAcrossPages()
    {
        var (ctx, svc) = NewContext();
        using var _ = ctx;
        var ids = Enumerable.Range(1, 25).Select(i => $"job-{i:00}").ToArray();
        Seed(svc, ids);

        var cut = RenderPage(ctx);
        var idButton = () => cut.FindAll("th").Single(th => th.TextContent.Trim() == "Id").QuerySelector("button")!;

        idButton().Click();
        idButton().Click();

        // Default page size is 20: descending, the first page starts at the last id overall.
        cut.WaitForAssertion(() =>
            Assert.Equal(ids.Reverse().Take(20), RowIds(cut)), TestTimeouts.RenderWait);

        cut.FindAll("button.page-link").Single(b => b.TextContent.Trim() == "2").Click();

        cut.WaitForAssertion(() =>
            Assert.Equal(ids.Reverse().Skip(20), RowIds(cut)), TestTimeouts.RenderWait);
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    private static RecurringJobDto Dto(
        string id, DateTime? next = null, DateTime? last = null, DateTime? created = null, Job job = null)
        => new() { Id = id, NextExecution = next, LastExecution = last, CreatedAt = created, Job = job };

    private static string[] Ids(IEnumerable<RecurringJobDto> jobs) => jobs.Select(j => j.Id).ToArray();

    private static (TestContext ctx, HangfireMonitorService svc) NewContext()
    {
        var storage = new InMemoryStorage();
        var options = new DashboardUIOptions { IsReadOnly = false, EnableJobManagement = true };
        var resolver = new JobMethodResolver();
        var svc = new HangfireMonitorService(storage, null, options, resolver);

        var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(resolver);
        ctx.Services.AddSingleton(svc);
        ctx.Services.AddSingleton(options);
        return (ctx, svc);
    }

    private static void Seed(HangfireMonitorService svc, params string[] jobIds)
    {
        foreach (var jobId in jobIds)
        {
            var result = svc.CreateOrUpdateRecurringJob(new RecurringJobRequest(
                JobId: jobId,
                TypeName: typeof(RecurringEditorFixtureJob).FullName,
                MethodName: nameof(RecurringEditorFixtureJob.DoNothing),
                ParameterJson: "[]",
                Cron: "0 0 * * *",
                Queue: "default",
                TimeZoneId: null,
                IsCustomMethod: false));
            Assert.True(result.Success, result.Error);
        }
    }

    private static IRenderedComponent<RecurringPage> RenderPage(TestContext ctx)
    {
        var cut = ctx.Render<RecurringPage>();
        cut.WaitForState(() => cut.FindAll("#recurring-filter").Count > 0, TestTimeouts.RenderWait);
        return cut;
    }

    private static string[] RowIds(IRenderedComponent<RecurringPage> cut) =>
        cut.FindAll("tbody tr td:nth-child(2) .hf-job-name").Select(e => e.TextContent.Trim()).ToArray();
}

public static class SortFixtureJob
{
    public static void Sync() { }
    public static void Report() { }
}
