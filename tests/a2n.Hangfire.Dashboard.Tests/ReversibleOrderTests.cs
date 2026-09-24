using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.States;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Helpers;
using a2n.Hangfire.Dashboard.Services;
using SucceededPage = a2n.Hangfire.Dashboard.Components.Pages.Jobs.Succeeded;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Newest/oldest toggle on the storage-paged job lists: a reversed page is read from the other end
/// of the storage list and flipped, so the order holds across every page.
/// </summary>
public class ReversibleOrderTests
{
    private static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, 25, 20)]  // first reversed page = the last 20 storage rows
    [InlineData(20, 5, 20)]
    [InlineData(40, 0, 5)]   // last, partial page
    public void StorageRange_Reversed_MapsToTheOtherEnd(int from, int expectedFrom, int expectedCount)
    {
        var order = new ReversibleOrder();
        order.Toggle();

        Assert.Equal((expectedFrom, expectedCount), order.StorageRange(total: 45, from, count: 20));
    }

    [Fact]
    public void StorageRange_NotReversed_IsUnchanged()
    {
        Assert.Equal((20, 20), new ReversibleOrder().StorageRange(total: 45, from: 20, count: 20));
    }

    [Fact]
    public void StorageRange_Reversed_PastTheEnd_IsEmpty()
    {
        var order = new ReversibleOrder();
        order.Toggle();

        Assert.Equal((0, 0), order.StorageRange(total: 10, from: 20, count: 20));
    }

    [Fact]
    public void Fetch_Reversed_AcrossAllPages_IsTheWholeListReversed()
    {
        var storage = Enumerable.Range(0, 45)
            .Select(i => new KeyValuePair<string, DateTime?>($"job-{i:00}", T0.AddMinutes(-i)))
            .ToList();
        JobList<DateTime?> FetchFromStorage(int from, int count) => new(storage.Skip(from).Take(count));

        var order = new ReversibleOrder();
        order.Toggle();

        var shown = new List<string>();
        for (var from = 0; from < storage.Count; from += 20)
            shown.AddRange(order.Fetch(FetchFromStorage, storage.Count, from, 20, t => t).Select(p => p.Key));

        Assert.Equal(storage.Select(p => p.Key).Reverse(), shown);
    }

    [Fact]
    public void Fetch_DetectsDirectionFromTimestamps()
    {
        var newestFirst = new JobList<DateTime?>(new[]
        {
            new KeyValuePair<string, DateTime?>("a", T0.AddMinutes(2)),
            new KeyValuePair<string, DateTime?>("b", null),
            new KeyValuePair<string, DateTime?>("c", T0),
        });
        var order = new ReversibleOrder();

        order.Fetch((_, _) => newestFirst, 3, 0, 20, t => t);
        Assert.True(order.NewestFirst);

        order.Toggle();
        Assert.False(order.NewestFirst);

        order.Fetch((_, _) => newestFirst, 3, 0, 20, t => t);
        Assert.False(order.NewestFirst);
    }

    [Fact]
    public void Observe_WithoutTwoDistinctTimestamps_LeavesDirectionUnknown()
    {
        var order = new ReversibleOrder();

        order.Observe(new DateTime?[] { T0, null, T0 });

        Assert.Null(order.NewestFirst);
    }

    [Fact]
    public void FetchIds_Reversed_FlipsThePage()
    {
        var ids = Enumerable.Range(0, 5).Select(i => $"id-{i}").ToList();
        var order = new ReversibleOrder();
        order.Toggle();

        var page = order.FetchIds((from, count) => ids.Skip(from).Take(count).ToList(), ids.Count, 0, 3);

        Assert.Equal(new[] { "id-4", "id-3", "id-2" }, page);
    }

    [Fact]
    public void SucceededPage_ToggleReversesTheWholeList()
    {
        var storage = new InMemoryStorage();
        var client = new BackgroundJobClient(storage);
        for (var i = 0; i < 25; i++)
            client.Create(Job.FromExpression(() => Console.WriteLine("x")), new SucceededState(null, 0, 0));

        var options = new DashboardUIOptions();
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(options);
        ctx.Services.AddSingleton(new HangfireMonitorService(storage, null, options, new JobMethodResolver()));

        var monitoring = storage.GetMonitoringApi();
        var storageOrder = monitoring.SucceededJobs(0, 25).Select(j => j.Key).ToArray();
        Assert.Equal(25, storageOrder.Length);

        var cut = ctx.Render<SucceededPage>();
        cut.WaitForState(() => RowIds(cut).Length == 20, TestTimeouts.RenderWait);
        Assert.Equal(storageOrder.Take(20), RowIds(cut));

        cut.Find("button[title='Sort by Succeeded']").Click();
        cut.WaitForAssertion(() =>
            Assert.Equal(storageOrder.Reverse().Take(20), RowIds(cut)), TestTimeouts.RenderWait);

        cut.FindAll("button.page-link").Single(b => b.TextContent.Trim() == "2").Click();
        cut.WaitForAssertion(() =>
            Assert.Equal(storageOrder.Reverse().Skip(20), RowIds(cut)), TestTimeouts.RenderWait);
    }

    private static string[] RowIds(IRenderedComponent<SucceededPage> cut) =>
        cut.FindAll("tbody tr td:nth-child(2) a").Select(a => a.GetAttribute("href")!.Split('/').Last()).ToArray();
}
