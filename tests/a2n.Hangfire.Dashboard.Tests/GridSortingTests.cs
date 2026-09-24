using System;
using System.Linq;
using Bunit;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.Server;
using Hangfire.States;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Helpers;
using a2n.Hangfire.Dashboard.Services;
using QueuesPage = a2n.Hangfire.Dashboard.Components.Pages.Queues;
using RecurringPage = a2n.Hangfire.Dashboard.Components.Pages.Recurring;
using ServersPage = a2n.Hangfire.Dashboard.Components.Pages.Servers;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Sorting on the in-memory grids beyond the main Recurring Jobs table: Stopped Jobs, Servers and
/// Queues, plus the shared <see cref="GridSortState{TColumn}"/> toggle.
/// </summary>
public class GridSortingTests
{
    private enum Column { None, A, B }

    [Fact]
    public void GridSortState_NewColumnSortsAscending_SameColumnFlips()
    {
        var sort = new GridSortState<Column>();
        Assert.True(sort.IsActive(Column.None));

        sort.Toggle(Column.A);
        Assert.True(sort.IsActive(Column.A));
        Assert.False(sort.Descending);

        sort.Toggle(Column.A);
        Assert.True(sort.Descending);

        sort.Toggle(Column.B);
        Assert.True(sort.IsActive(Column.B));
        Assert.False(sort.Descending);
    }

    [Fact]
    public void StoppedJobs_SortById()
    {
        var storage = new InMemoryStorage();
        var options = new DashboardUIOptions { IsReadOnly = false, EnableJobManagement = true };
        var resolver = new JobMethodResolver();
        var svc = new HangfireMonitorService(storage, null, options, resolver);
        foreach (var id in new[] { "beta", "alpha", "gamma" })
        {
            var result = svc.CreateOrUpdateRecurringJob(new RecurringJobRequest(
                JobId: id,
                TypeName: typeof(RecurringEditorFixtureJob).FullName,
                MethodName: nameof(RecurringEditorFixtureJob.DoNothing),
                ParameterJson: "[]",
                Cron: "0 0 * * *",
                Queue: "default",
                TimeZoneId: null,
                IsCustomMethod: false));
            Assert.True(result.Success, result.Error);
            svc.StopRecurringJob(id);
        }

        using var ctx = NewContext();
        ctx.Services.AddSingleton(resolver);
        ctx.Services.AddSingleton(svc);
        ctx.Services.AddSingleton(options);

        var cut = ctx.RenderComponent<RecurringPage>();
        cut.WaitForState(() => cut.Markup.Contains("Stopped Jobs"), TestTimeouts.RenderWait);

        string[] StoppedIds() =>
            cut.FindAll("tr.table-secondary td:nth-child(1) .hf-job-name").Select(e => e.TextContent.Trim()).ToArray();
        void ClickStoppedId() => cut.FindAll("button[title='Sort by Id']").First().Click();

        ClickStoppedId();
        cut.WaitForAssertion(() => Assert.Equal(new[] { "alpha", "beta", "gamma" }, StoppedIds()), TestTimeouts.RenderWait);

        ClickStoppedId();
        cut.WaitForAssertion(() => Assert.Equal(new[] { "gamma", "beta", "alpha" }, StoppedIds()), TestTimeouts.RenderWait);
    }

    [Fact]
    public void Servers_SortByWorkers()
    {
        var storage = new InMemoryStorage();
        using (var connection = storage.GetConnection())
        {
            connection.AnnounceServer("server-a", new ServerContext { WorkerCount = 5, Queues = new[] { "default" } });
            connection.AnnounceServer("server-b", new ServerContext { WorkerCount = 20, Queues = new[] { "default" } });
            connection.AnnounceServer("server-c", new ServerContext { WorkerCount = 1, Queues = new[] { "default" } });
        }

        using var ctx = NewContext();
        var options = new DashboardUIOptions();
        ctx.Services.AddSingleton(options);
        ctx.Services.AddSingleton(new HangfireMonitorService(storage, null, options, new JobMethodResolver()));

        var cut = ctx.RenderComponent<ServersPage>();
        cut.WaitForState(() => cut.FindAll("tbody tr").Count == 3, TestTimeouts.RenderWait);

        string[] Workers() => cut.FindAll("tbody tr td:nth-child(2)").Select(e => e.TextContent.Trim()).ToArray();

        cut.Find("button[title='Sort by Workers']").Click();
        cut.WaitForAssertion(() => Assert.Equal(new[] { "1", "5", "20" }, Workers()), TestTimeouts.RenderWait);

        cut.Find("button[title='Sort by Workers']").Click();
        cut.WaitForAssertion(() => Assert.Equal(new[] { "20", "5", "1" }, Workers()), TestTimeouts.RenderWait);
    }

    [Fact]
    public void Queues_DefaultByName_CanSortByEnqueuedDescending()
    {
        var storage = new InMemoryStorage();
        var client = new BackgroundJobClient(storage);
        void Enqueue(string queue, int count)
        {
            for (var i = 0; i < count; i++)
                client.Create(Job.FromExpression(() => Console.WriteLine("x")), new EnqueuedState(queue));
        }
        Enqueue("alpha", 1);
        Enqueue("beta", 5);
        Enqueue("gamma", 3);

        using var ctx = NewContext();
        var options = new DashboardUIOptions();
        ctx.Services.AddSingleton(options);
        ctx.Services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(
            new Microsoft.AspNetCore.Http.HttpContextAccessor());
        ctx.Services.AddScoped<AuditActorAccessor>();
        ctx.Services.AddScoped(sp => new AuditLogService(
            storage, options, sp.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>(),
            sp.GetService<AuditActorAccessor>()));
        ctx.Services.AddScoped(sp => new QueueOperationsService(
            storage, options, sp.GetRequiredService<AuditLogService>(), sp.GetService<AuditActorAccessor>()));
        ctx.Services.AddSingleton<QueueOperationsStateCache>();
        ctx.Services.AddSingleton(new HangfireMonitorService(storage, null, options, new JobMethodResolver()));

        var cut = ctx.RenderComponent<QueuesPage>();
        cut.WaitForState(() => cut.FindAll(".hf-queue-name code").Count == 3, TestTimeouts.RenderWait);

        string[] Names() => cut.FindAll(".hf-queue-name code").Select(e => e.TextContent.Trim()).ToArray();

        Assert.Equal(new[] { "alpha", "beta", "gamma" }, Names());

        cut.Find("#queue-sort").Change("Enqueued");
        cut.WaitForAssertion(() => Assert.Equal(new[] { "alpha", "gamma", "beta" }, Names()), TestTimeouts.RenderWait);

        cut.Find("#queue-sort-direction").Click();
        cut.WaitForAssertion(() => Assert.Equal(new[] { "beta", "gamma", "alpha" }, Names()), TestTimeouts.RenderWait);
    }

    private static TestContext NewContext()
    {
        var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }
}
