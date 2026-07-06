using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Example-based unit tests for <see cref="HeatmapService"/>'s historical-source wiring and graceful
/// degradation (task 13.4). These cover behavior that is not a universal property: source-toggle
/// availability, the 10-second timeout revert to the Projected source with a dismissible notice, and
/// the treatment of zero-fire historical buckets as no-data.
///
/// The service is constructed without a <c>JobStorage</c>, so the Projected fallback yields an
/// empty matrix — sufficient to assert the degradation contract (the notice and the revert) without
/// standing up Hangfire storage.
/// </summary>
public class HeatmapServiceTests
{
    private static HeatmapService CreateService(
        IStorageMetricsProvider provider = null,
        int historicalTimeoutSeconds = 10)
    {
        var services = new ServiceCollection();
        if (provider != null)
        {
            services.AddSingleton(provider);
        }

        services.AddLogging();
        services.AddSingleton<IMemoryCache>(new MemoryCache(new MemoryCacheOptions()));

        var options = new DashboardUIOptions();
        options.Heatmap.HistoricalQueryTimeoutSeconds = historicalTimeoutSeconds;
        services.AddSingleton(options);

        var sp = services.BuildServiceProvider();
        return new HeatmapService(sp);
    }

    private static HeatmapQuery HistoricalQuery(LoadMetric metric = LoadMetric.FireCount) => new(
        Source: HeatmapSource.Historical,
        WindowKind: ProjectionWindowKind.IdealizedWeek,
        ViewerTimeZoneId: null,
        JobClass: "Cron",
        LoadMetric: metric,
        TopN: 10,
        HideSubHourly: false,
        LogScale: false,
        LookbackWeeks: 4,
        AggregationStatistic: "Average",
        ManualCapacity: null);

    // ─── Source availability (Req 7.1 / 7.2) ────────────────────────────────────

    [Fact]
    public void IsHistoricalAvailable_IsFalse_WhenNoProviderRegistered()
    {
        var service = CreateService(provider: null);
        Assert.False(service.IsHistoricalAvailable);
    }

    [Fact]
    public void IsHistoricalAvailable_IsTrue_WhenProviderRegistered()
    {
        var service = CreateService(new Mock<IStorageMetricsProvider>().Object);
        Assert.True(service.IsHistoricalAvailable);
    }

    [Fact]
    public async Task HistoricalRequest_WithoutProvider_RendersProjectedWithoutError()
    {
        // No provider registered → the toggle is hidden and any Historical request renders exclusively
        // from the Projected source with no error notice (Req 7.2).
        var service = CreateService(provider: null);

        var result = await service.GetMatrixAsync(HistoricalQuery(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Null(result.HistoricalError);
        Assert.Empty(result.Matrix.Cells);
    }

    // ─── Successful historical aggregation (Req 7.4) ─────────────────────────────

    [Fact]
    public async Task HistoricalRequest_WithBuckets_BuildsMatrixAndExcludesZeroFireBuckets()
    {
        var buckets = new List<HistoricalScheduleBucket>
        {
            new() { Queue = "default", DayIndex = 0, Hour = 9, FireCount = 5, FailureCount = 1, AvgMs = 30_000 },
            new() { Queue = "critical", DayIndex = 2, Hour = 14, FireCount = 3, FailureCount = 0, AvgMs = 120_000 },
            // Zero-fire bucket must be treated as no-data (Req 7.4), i.e. produce no cell.
            new() { Queue = "default", DayIndex = 1, Hour = 0, FireCount = 0, FailureCount = 0, AvgMs = 0 },
        };

        var mock = new Mock<IStorageMetricsProvider>();
        mock.Setup(m => m.GetRecurringScheduleBucketsAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(buckets);

        var service = CreateService(mock.Object);
        var result = await service.GetMatrixAsync(HistoricalQuery(), CancellationToken.None);

        Assert.Null(result.HistoricalError);
        Assert.Equal(2, result.Matrix.Cells.Count); // the zero-fire bucket is excluded

        var fireCell = result.Matrix.Cells[new CellKey("default", 0, 9)];
        Assert.Equal(5d, fireCell.Value);

        // The zero-fire bucket produced no cell at all (no-data, not a zero value).
        Assert.False(result.Matrix.Cells.ContainsKey(new CellKey("default", 1, 0)));
    }

    [Fact]
    public async Task HistoricalRequest_WorkerMinutes_ScalesByAverageDuration()
    {
        var buckets = new List<HistoricalScheduleBucket>
        {
            // 4 fires averaging 120_000 ms (2 minutes) → 8 worker-minutes.
            new() { Queue = "default", DayIndex = 0, Hour = 0, FireCount = 4, FailureCount = 0, AvgMs = 120_000 },
            // 2 fires averaging 6_000 ms (0.1 min) → clamped to 1 minute each → 2 worker-minutes.
            new() { Queue = "default", DayIndex = 0, Hour = 1, FireCount = 2, FailureCount = 0, AvgMs = 6_000 },
        };

        var mock = new Mock<IStorageMetricsProvider>();
        mock.Setup(m => m.GetRecurringScheduleBucketsAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(buckets);

        var service = CreateService(mock.Object);
        var result = await service.GetMatrixAsync(HistoricalQuery(LoadMetric.WorkerMinutes), CancellationToken.None);

        Assert.Equal(8d, result.Matrix.Cells[new CellKey("default", 0, 0)].Value, precision: 6);
        Assert.Equal(2d, result.Matrix.Cells[new CellKey("default", 0, 1)].Value, precision: 6);
    }

    // ─── Graceful degradation on failure / timeout (Req 7.5) ─────────────────────

    [Fact]
    public async Task HistoricalRequest_WhenProviderThrows_RevertsToProjectedWithNotice()
    {
        var mock = new Mock<IStorageMetricsProvider>();
        mock.Setup(m => m.GetRecurringScheduleBucketsAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB error"));

        var service = CreateService(mock.Object);
        var result = await service.GetMatrixAsync(HistoricalQuery(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result.HistoricalError)); // dismissible notice surfaced
        Assert.Empty(result.Matrix.Cells); // reverted to the (empty) Projected matrix
    }

    [Fact]
    public async Task HistoricalRequest_WhenProviderExceedsTimeout_RevertsToProjectedWithNotice()
    {
        var mock = new Mock<IStorageMetricsProvider>();
        mock.Setup(m => m.GetRecurringScheduleBucketsAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(async (DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
            {
                // Exceed the 1-second timeout configured below; the service must stop waiting.
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return (IReadOnlyList<HistoricalScheduleBucket>)Array.Empty<HistoricalScheduleBucket>();
            });

        var service = CreateService(mock.Object, historicalTimeoutSeconds: 1);

        var start = DateTimeOffset.UtcNow;
        var result = await service.GetMatrixAsync(HistoricalQuery(), CancellationToken.None);
        var elapsed = DateTimeOffset.UtcNow - start;

        Assert.False(string.IsNullOrEmpty(result.HistoricalError));
        Assert.Empty(result.Matrix.Cells);
        // The timeout must bound the wait well below the provider's 10-second delay.
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"Expected timeout to fire quickly but waited {elapsed}.");
    }
}
