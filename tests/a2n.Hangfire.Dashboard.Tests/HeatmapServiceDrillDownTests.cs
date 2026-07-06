using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services;
using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Example-based unit tests for <see cref="HeatmapService.GetCellJobsAsync"/> (task 20.2). These
/// cover the drill-down resolution behavior that is not a universal property:
/// <list type="bullet">
/// <item>a cell with no contributing jobs (no recurring jobs at all) yields an empty, error-free
/// result so the drawer never opens for an empty cell (Req 10.1, 10.2);</item>
/// <item>the method never throws — invalid inputs and internal failures are surfaced as a
/// <see cref="DrillDownResult.Error"/> so the page retains its heatmap unchanged (Req 10.7).</item>
/// </list>
/// The contributing-jobs matching and next-run ordering over real recurring data are exercised by
/// the pure-engine projection/aggregation suites and the drawer's own bUnit tests (task 20.3).
/// </summary>
public class HeatmapServiceDrillDownTests
{
    private const string RecurringJobsSetKey = "recurring-jobs";

    private static HeatmapService CreateService(
        IEnumerable<string> recurringJobIds = null,
        bool throwOnConnection = false)
    {
        var ids = new HashSet<string>(recurringJobIds ?? Enumerable.Empty<string>());

        var connection = new Mock<IStorageConnection>();
        connection.Setup(c => c.GetAllItemsFromSet(RecurringJobsSetKey)).Returns(ids);

        var storage = new Mock<JobStorage>();
        if (throwOnConnection)
        {
            storage.Setup(s => s.GetConnection()).Throws(new InvalidOperationException("storage unreadable"));
        }
        else
        {
            storage.Setup(s => s.GetConnection()).Returns(connection.Object);
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMemoryCache>(new MemoryCache(new MemoryCacheOptions()));
        services.AddSingleton<JobStorage>(storage.Object);
        services.AddSingleton(new HangfireMonitorService(storage.Object));
        services.AddSingleton(new DashboardUIOptions());

        return new HeatmapService(services.BuildServiceProvider());
    }

    private static HeatmapQuery ProjectedQuery() => new(
        Source: HeatmapSource.Projected,
        WindowKind: ProjectionWindowKind.IdealizedWeek,
        ViewerTimeZoneId: null,
        JobClass: "Cron",
        LoadMetric: LoadMetric.FireCount,
        TopN: 10,
        HideSubHourly: false,
        LogScale: false,
        LookbackWeeks: 4,
        AggregationStatistic: "Average",
        ManualCapacity: null);

    [Fact]
    public async Task GetCellJobs_WithNoRecurringJobs_ReturnsEmptyResultWithoutError()
    {
        // No recurring jobs → no job contributes to the cell, so the drawer must receive an empty,
        // error-free result and stay closed (Req 10.1, 10.2).
        var service = CreateService(recurringJobIds: Array.Empty<string>());

        var result = await service.GetCellJobsAsync(
            new CellKey("default", 0, 9), ProjectedQuery(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.Jobs);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task GetCellJobs_WithNullQuery_ReturnsErrorAndDoesNotThrow()
    {
        // A null query must surface as a drawer notice rather than throwing (Req 10.7).
        var service = CreateService();

        var result = await service.GetCellJobsAsync(
            new CellKey("default", 0, 9), query: null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.Jobs);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task GetCellJobs_WithNullKey_ReturnsErrorAndDoesNotThrow()
    {
        // A null cell key must surface as a drawer notice rather than throwing (Req 10.7).
        var service = CreateService();

        var result = await service.GetCellJobsAsync(
            key: null, ProjectedQuery(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.Jobs);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task GetCellJobs_WhenStorageUnreadable_ReturnsEmptyResultWithoutThrowing()
    {
        // An unreadable storage is swallowed when reading recurring jobs (Req 1.7), so the drill-down
        // resolves to an empty, error-free result and never throws (Req 10.7).
        var service = CreateService(throwOnConnection: true);

        var result = await service.GetCellJobsAsync(
            new CellKey("default", 0, 9), ProjectedQuery(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.Jobs);
        Assert.Null(result.Error);
    }
}
