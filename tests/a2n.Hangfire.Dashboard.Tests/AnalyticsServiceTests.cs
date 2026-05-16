using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests;

public class AnalyticsServiceTests
{
    private static AnalyticsService CreateService(IStorageMetricsProvider provider = null)
    {
        var services = new ServiceCollection();
        if (provider != null)
            services.AddSingleton(provider);
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        return new AnalyticsService(sp);
    }

    // ─── IsAvailable ────────────────────────────────────────────────────────────

    [Fact]
    public void IsAvailable_ReturnsFalse_WhenNoProviderRegistered()
    {
        var service = CreateService(provider: null);
        Assert.False(service.IsAvailable);
    }

    [Fact]
    public void IsAvailable_ReturnsTrue_WhenProviderRegistered()
    {
        var mock = new Mock<IStorageMetricsProvider>();
        var service = CreateService(mock.Object);
        Assert.True(service.IsAvailable);
    }

    // ─── SelectInterval ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 30, MetricsInterval.OneMinute)]       // 30 min → OneMinute
    [InlineData(0, 60, MetricsInterval.OneMinute)]       // exactly 1h → OneMinute
    [InlineData(0, 120, MetricsInterval.FiveMinutes)]    // 2h → FiveMinutes
    [InlineData(0, 360, MetricsInterval.FiveMinutes)]    // exactly 6h → FiveMinutes
    [InlineData(0, 720, MetricsInterval.FifteenMinutes)] // 12h → FifteenMinutes
    [InlineData(0, 1440, MetricsInterval.FifteenMinutes)]// exactly 24h → FifteenMinutes
    public void SelectInterval_ReturnsCorrectInterval_ForMinuteRanges(int startMin, int endMin, MetricsInterval expected)
    {
        var from = DateTimeOffset.UtcNow;
        var to = from.AddMinutes(endMin - startMin);
        Assert.Equal(expected, AnalyticsService.SelectInterval(from, to));
    }

    [Fact]
    public void SelectInterval_ReturnsOneHour_ForRangeUpTo7Days()
    {
        var from = DateTimeOffset.UtcNow;
        var to = from.AddDays(3);
        Assert.Equal(MetricsInterval.OneHour, AnalyticsService.SelectInterval(from, to));
    }

    [Fact]
    public void SelectInterval_ReturnsOneHour_ForExactly7Days()
    {
        var from = DateTimeOffset.UtcNow;
        var to = from.AddDays(7);
        Assert.Equal(MetricsInterval.OneHour, AnalyticsService.SelectInterval(from, to));
    }

    [Fact]
    public void SelectInterval_ReturnsOneDay_ForRangeOver7Days()
    {
        var from = DateTimeOffset.UtcNow;
        var to = from.AddDays(14);
        Assert.Equal(MetricsInterval.OneDay, AnalyticsService.SelectInterval(from, to));
    }

    // ─── ComputeFailureRatePercent ──────────────────────────────────────────────

    [Fact]
    public void ComputeFailureRatePercent_ReturnsZero_WhenTotalIsZero()
    {
        Assert.Equal(0.0, AnalyticsService.ComputeFailureRatePercent(0, 0));
    }

    [Fact]
    public void ComputeFailureRatePercent_ReturnsZero_WhenTotalIsNegative()
    {
        Assert.Equal(0.0, AnalyticsService.ComputeFailureRatePercent(5, -1));
    }

    [Fact]
    public void ComputeFailureRatePercent_Returns100_WhenAllFailed()
    {
        Assert.Equal(100.0, AnalyticsService.ComputeFailureRatePercent(10, 10));
    }

    [Fact]
    public void ComputeFailureRatePercent_ReturnsCorrectPercentage_WithOneDecimal()
    {
        // 1 out of 3 = 33.333...% → rounded to 33.3
        Assert.Equal(33.3, AnalyticsService.ComputeFailureRatePercent(1, 3));
    }

    [Fact]
    public void ComputeFailureRatePercent_Returns50_ForHalf()
    {
        Assert.Equal(50.0, AnalyticsService.ComputeFailureRatePercent(5, 10));
    }

    // ─── Error Handling (wrapper methods return defaults on exception) ───────────

    [Fact]
    public async Task GetThroughputTimelineAsync_ReturnsEmpty_WhenProviderThrows()
    {
        var mock = new Mock<IStorageMetricsProvider>();
        mock.Setup(m => m.GetThroughputTimelineAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<MetricsInterval>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB error"));

        var service = CreateService(mock.Object);
        var result = await service.GetThroughputTimelineAsync(
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, MetricsInterval.OneMinute);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetThroughputTimelineAsync_ReturnsEmpty_WhenNoProvider()
    {
        var service = CreateService(provider: null);
        var result = await service.GetThroughputTimelineAsync(
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, MetricsInterval.OneMinute);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetThroughputTimelineAsync_ReturnsData_WhenProviderSucceeds()
    {
        var expected = new List<ThroughputDataPoint>
        {
            new() { Timestamp = DateTimeOffset.UtcNow, Succeeded = 10, Failed = 2, Deleted = 1 }
        };

        var mock = new Mock<IStorageMetricsProvider>();
        mock.Setup(m => m.GetThroughputTimelineAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<MetricsInterval>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var service = CreateService(mock.Object);
        var result = await service.GetThroughputTimelineAsync(
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, MetricsInterval.OneMinute);

        Assert.Single(result);
        Assert.Equal(10, result[0].Succeeded);
    }

    [Fact]
    public async Task GetServerUtilizationSnapshotAsync_ReturnsEmptySnapshot_WhenProviderThrows()
    {
        var mock = new Mock<IStorageMetricsProvider>();
        mock.Setup(m => m.GetServerUtilizationSnapshotAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Connection timeout"));

        var service = CreateService(mock.Object);
        var result = await service.GetServerUtilizationSnapshotAsync();

        Assert.NotNull(result);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data);
    }

    [Fact]
    public async Task GetAverageStateTimingsAsync_ReturnsZeroedTimings_WhenProviderThrows()
    {
        var mock = new Mock<IStorageMetricsProvider>();
        mock.Setup(m => m.GetAverageStateTimingsAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Unexpected error"));

        var service = CreateService(mock.Object);
        var result = await service.GetAverageStateTimingsAsync(
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Equal(0.0, result.AvgScheduledMs);
        Assert.Equal(0.0, result.AvgEnqueuedMs);
        Assert.Equal(0.0, result.AvgProcessingMs);
    }

    [Fact]
    public async Task GetRecurringJobExecutionsAsync_ReturnsEmpty_WhenNoProvider()
    {
        var service = CreateService(provider: null);
        var result = await service.GetRecurringJobExecutionsAsync("test-job", 10);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetQueueDepthSnapshotAsync_ReturnsEmptySnapshot_WhenNoProvider()
    {
        var service = CreateService(provider: null);
        var result = await service.GetQueueDepthSnapshotAsync();

        Assert.NotNull(result);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data);
    }
}
