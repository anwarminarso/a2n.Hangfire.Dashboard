using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Models;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using HeatmapService = a2n.Hangfire.Dashboard.Services.HeatmapService;
using HangfireMonitorService = a2n.Hangfire.Dashboard.Services.HangfireMonitorService;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Example-based unit tests for the <see cref="HeatmapService"/> view-data methods that drive the
/// page beyond the matrix — <c>GetRecurringJobSpecsAsync</c>, <c>GetWorstConcurrencyDayAsync</c>,
/// <c>GetConcurrencyAsync</c>, <c>GetRecommendationsAsync</c>, <c>GetDemandProfile</c>, and
/// <c>GetHistoricalCellsAsync</c>. These were previously only verified at runtime (heatmap backlog
/// #1); here they run against a real <see cref="InMemoryStorage"/> with registered recurring jobs so
/// the projected pipeline produces deterministic, assertable view data without a database.
/// </summary>
public class HeatmapServiceViewDataTests
{
    /// <summary>
    /// Builds storage with three hourly jobs (which overlap at each hour's minute 0, forming a
    /// cluster of three so a stagger recommendation is produced) plus one daily job.
    /// </summary>
    private static JobStorage BuildStorage()
    {
        var storage = new InMemoryStorage();
        var manager = new RecurringJobManager(storage);

        manager.AddOrUpdate("hourly-a", Job.FromExpression(() => HeatmapTestJobs.NoOp()), "0 * * * *", new RecurringJobOptions());
        manager.AddOrUpdate("hourly-b", Job.FromExpression(() => HeatmapTestJobs.NoOp()), "0 * * * *", new RecurringJobOptions());
        manager.AddOrUpdate("hourly-c", Job.FromExpression(() => HeatmapTestJobs.NoOp()), "0 * * * *", new RecurringJobOptions());
        manager.AddOrUpdate("daily-x", Job.FromExpression(() => HeatmapTestJobs.NoOp()), "0 0 * * *", new RecurringJobOptions());

        return storage;
    }

    private static HeatmapService CreateService(JobStorage storage)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMemoryCache>(new MemoryCache(new MemoryCacheOptions()));
        services.AddSingleton(storage);
        services.AddSingleton(new HangfireMonitorService(storage));
        services.AddSingleton(new DashboardUIOptions());
        return new HeatmapService(services.BuildServiceProvider());
    }

    private static HeatmapQuery Query() => new(
        Source: HeatmapSource.Projected,
        WindowKind: ProjectionWindowKind.IdealizedWeek,
        ViewerTimeZoneId: null,
        JobClass: "Cron",
        LoadMetric: LoadMetric.FireCount,
        TopN: 100,
        HideSubHourly: false,
        LogScale: false,
        LookbackWeeks: 4,
        AggregationStatistic: "Average",
        ManualCapacity: null);

    [Fact]
    public async Task GetRecurringJobSpecs_ReturnsEveryRegisteredJob()
    {
        var service = CreateService(BuildStorage());

        var specs = await service.GetRecurringJobSpecsAsync(Query(), CancellationToken.None);

        Assert.Equal(4, specs.Count);
        Assert.Contains(specs, s => s.JobId == "hourly-a" && s.CronExpression == "0 * * * *");
        Assert.Contains(specs, s => s.JobId == "daily-x" && s.CronExpression == "0 0 * * *");
        // No metrics provider → estimated durations fall back to the flagged default (≥ 1 minute).
        Assert.All(specs, s => Assert.True(s.EstimatedDuration >= TimeSpan.FromMinutes(1)));
        Assert.All(specs, s => Assert.True(s.EstimatedDurationIsDefault));
    }

    [Fact]
    public async Task GetWorstConcurrencyDay_ReturnsAValidWindowDay()
    {
        var service = CreateService(BuildStorage());

        var day = await service.GetWorstConcurrencyDayAsync(Query(), workerCapacity: 10, CancellationToken.None);

        Assert.InRange(day, 0, 6);
    }

    [Fact]
    public async Task GetConcurrency_CountsOverlappingHourlyFires()
    {
        var service = CreateService(BuildStorage());

        // At minute 0 of every hour the three hourly jobs (1-minute duration each) overlap, so the
        // cron-only peak concurrency for any day is at least three.
        var result = await service.GetConcurrencyAsync(
            Query(), dayIndex: 0, workerCapacity: 10, adHocBaselinePerSlot: null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.PeakConcurrency >= 3,
            $"Expected peak concurrency ≥ 3 from the three overlapping hourly jobs, got {result.PeakConcurrency}.");
    }

    [Fact]
    public async Task GetRecommendations_ProducesAStaggerSuggestionForTheOverlappingCluster()
    {
        var service = CreateService(BuildStorage());

        var recommendations = await service.GetRecommendationsAsync(
            Query(), workerCapacity: 1, adHocBaselinePerSlot: null, CancellationToken.None);

        // The three coincident hourly fires form a cluster of three; staggering them across the day
        // strictly reduces the peak, so a recommendation is presented (Req 11.1–11.3).
        Assert.NotEmpty(recommendations);
        Assert.All(recommendations, r => Assert.True(r.StaggeredPeak < r.CurrentPeak));
        // Detected peak (3) exceeds the worker capacity (1) → high severity (Req 11.5).
        Assert.Contains(recommendations, r => r.Severity == RecommendationSeverity.High);
    }

    [Fact]
    public async Task GetDemandProfile_WithoutMetricsProvider_IsEmpty()
    {
        var service = CreateService(BuildStorage());

        var profile = service.GetDemandProfile(Query());

        // No metrics provider → the demand profile degrades to empty (Req 16.7).
        Assert.NotNull(profile);
        Assert.Empty(profile.Slots);
        Assert.False(service.IsDemandAvailable);
    }

    [Fact]
    public async Task GetHistoricalCells_WithoutMetricsProvider_IsEmpty()
    {
        var service = CreateService(BuildStorage());

        var cells = await service.GetHistoricalCellsAsync(Query(), CancellationToken.None);

        // No metrics provider → no historical cells; the views then render the empty shade (Req 7.4).
        Assert.Empty(cells);
        Assert.False(service.IsHistoricalAvailable);
    }

    [Fact]
    public async Task GetConcurrency_WithQueueFilterExcludingAll_YieldsNoOverlap()
    {
        var service = CreateService(BuildStorage());

        // All jobs are on "default"; filtering to a non-existent queue removes every fire, so the
        // concurrency collapses to zero — exercising the queue-filter parameter path.
        var result = await service.GetConcurrencyAsync(
            Query(), dayIndex: 0, workerCapacity: 10, adHocBaselinePerSlot: null, CancellationToken.None,
            queues: new[] { "nonexistent-queue" });

        Assert.NotNull(result);
        Assert.Equal(0, result.PeakConcurrency);
    }
}
