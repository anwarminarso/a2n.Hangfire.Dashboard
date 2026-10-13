using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using Hangfire;
using Hangfire.InMemory;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Components.Pages;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Tests for <see cref="DashboardMetricsFeed"/>, the in-process channel that replaced the dashboard
/// components' SignalR client connection back to the server's own URL (issue #61).
/// </summary>
public class DashboardMetricsFeedTests
{
    private static readonly TimeSpan DeliveryWait = TimeSpan.FromSeconds(10);

    private readonly JobStorage _storage = new InMemoryStorage();

    private ServiceProvider BuildProvider(int statsPollingInterval = 2000)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new DashboardUIOptions { StatsPollingInterval = statsPollingInterval });
        services.AddSingleton<DashboardSubscriptionTracker>();
        services.AddScoped(_ => new HangfireMonitorService(_storage));
        services.AddSingleton<DashboardMetricsFeed>();
        return services.BuildServiceProvider();
    }

    private static DashboardMetricsSnapshot Snapshot(long enqueued = 0, DateTime? capturedAt = null) => new()
    {
        Statistics = new StatisticsDto { Enqueued = enqueued },
        Servers = [],
        CapturedAtUtc = capturedAt ?? DateTime.UtcNow,
    };

    [Fact]
    public async Task GetMetrics_WithinInterval_ReusesSnapshot()
    {
        using var sp = BuildProvider();
        var feed = sp.GetRequiredService<DashboardMetricsFeed>();

        var first = await feed.GetMetricsAsync();
        var second = await feed.GetMetricsAsync();

        Assert.NotNull(first.Statistics);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task GetMetrics_ConcurrentCallers_ShareOneRead()
    {
        // N tabs opening at once (each with a prerender and an interactive render) must cost one read.
        using var sp = BuildProvider();
        var feed = sp.GetRequiredService<DashboardMetricsFeed>();

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => feed.GetMetricsAsync()));

        Assert.All(results, r => Assert.Same(results[0], r));
    }

    [Fact]
    public async Task GetMetrics_StaleSnapshot_ReadsAgain()
    {
        using var sp = BuildProvider();
        var feed = sp.GetRequiredService<DashboardMetricsFeed>();
        var stale = Snapshot(capturedAt: DateTime.UtcNow.AddMinutes(-1));
        feed.PublishMetrics(stale);

        var result = await feed.GetMetricsAsync();

        Assert.NotSame(stale, result);
    }

    [Fact]
    public async Task GetMetrics_ReturnsPublishedSnapshot_WhileFresh()
    {
        using var sp = BuildProvider();
        var feed = sp.GetRequiredService<DashboardMetricsFeed>();
        var published = Snapshot(enqueued: 7);
        feed.PublishMetrics(published);

        Assert.Same(published, await feed.GetMetricsAsync());
    }

    [Fact]
    public async Task Refresh_AlwaysReadsFreshSnapshot()
    {
        using var sp = BuildProvider();
        var feed = sp.GetRequiredService<DashboardMetricsFeed>();

        var cached = await feed.GetMetricsAsync();
        var refreshed = await feed.RefreshAsync();

        Assert.NotSame(cached, refreshed);
        Assert.Same(refreshed, feed.Latest);
    }

    [Fact]
    public void Subscriptions_GateTheBroadcast_ThroughTheSharedTracker()
    {
        using var sp = BuildProvider();
        var feed = sp.GetRequiredService<DashboardMetricsFeed>();
        var tracker = sp.GetRequiredService<DashboardSubscriptionTracker>();

        var metrics = feed.SubscribeMetrics(_ => Task.CompletedTask);
        var analytics = feed.SubscribeAnalytics(_ => Task.CompletedTask);
        Assert.True(tracker.HasMetricsSubscribers);
        Assert.True(tracker.HasAnalyticsSubscribers);

        metrics.Dispose();
        metrics.Dispose(); // idempotent: must not decrement twice
        analytics.Dispose();

        Assert.False(tracker.HasMetricsSubscribers);
        Assert.False(tracker.HasAnalyticsSubscribers);
        Assert.Equal(0, feed.MetricsSubscriberCount);
        Assert.Equal(0, feed.AnalyticsSubscriberCount);
    }

    [Fact]
    public async Task PublishMetrics_DeliversToEverySubscriber_EvenWhenOneThrows()
    {
        using var sp = BuildProvider();
        var feed = sp.GetRequiredService<DashboardMetricsFeed>();
        var received = new ConcurrentBag<DashboardMetricsSnapshot>();
        var a = new TaskCompletionSource();
        var b = new TaskCompletionSource();

        using var broken = feed.SubscribeMetrics(_ => throw new ObjectDisposedException("circuit"));
        using var first = feed.SubscribeMetrics(s => { received.Add(s); a.TrySetResult(); return Task.CompletedTask; });
        using var second = feed.SubscribeMetrics(s => { received.Add(s); b.TrySetResult(); return Task.CompletedTask; });

        var snapshot = Snapshot(enqueued: 3);
        feed.PublishMetrics(snapshot);

        await Task.WhenAll(a.Task, b.Task).WaitAsync(DeliveryWait);
        Assert.Equal(2, received.Count);
        Assert.All(received, s => Assert.Same(snapshot, s));
        Assert.Same(snapshot, feed.Latest);
    }

    [Fact]
    public async Task PublishMetrics_SkipsDisposedSubscriptions()
    {
        using var sp = BuildProvider();
        var feed = sp.GetRequiredService<DashboardMetricsFeed>();
        var calls = 0;
        var live = new TaskCompletionSource();

        var gone = feed.SubscribeMetrics(_ => { calls++; return Task.CompletedTask; });
        gone.Dispose();
        using var kept = feed.SubscribeMetrics(_ => { live.TrySetResult(); return Task.CompletedTask; });

        feed.PublishMetrics(Snapshot());

        await live.Task.WaitAsync(DeliveryWait);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task PublishAnalytics_DeliversPayload()
    {
        using var sp = BuildProvider();
        var feed = sp.GetRequiredService<DashboardMetricsFeed>();
        var delivered = new TaskCompletionSource<AnalyticsBroadcastPayload>();
        using var sub = feed.SubscribeAnalytics(p => { delivered.TrySetResult(p); return Task.CompletedTask; });

        var payload = new AnalyticsBroadcastPayload();
        feed.PublishAnalytics(payload);

        Assert.Same(payload, await delivered.Task.WaitAsync(DeliveryWait));
    }

    [Theory]
    [InlineData(2000, 2000)]
    [InlineData(5000, 5000)]
    [InlineData(100, 500)]
    [InlineData(0, 500)]
    [InlineData(-1, 500)]
    public void Interval_HonorsStatsPollingInterval_WithLowerBound(int configuredMs, int expectedMs)
    {
        using var sp = BuildProvider(configuredMs);
        var feed = sp.GetRequiredService<DashboardMetricsFeed>();

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), feed.Interval);
    }

    [Fact]
    public void HubPayload_KeepsWireFormat()
    {
        var snapshot = new DashboardMetricsSnapshot
        {
            Statistics = new StatisticsDto
            {
                Servers = 1, Recurring = 2, Enqueued = 3, Processing = 4,
                Succeeded = 5, Failed = 6, Scheduled = 7, Deleted = 8,
            },
        };

        var payload = MetricsBroadcastService.ToHubPayload(snapshot);

        Assert.Equal(
            new[] { "servers", "recurring", "enqueued", "processing", "succeeded", "failed", "scheduled", "deleted" }.Order(),
            payload.Keys.Order());
        Assert.Equal(3L, payload["enqueued"]);
        Assert.Equal(8L, payload["deleted"]);
    }

    [Fact]
    public async Task HomePage_RendersBroadcastPayload_AndUnsubscribesOnDispose()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(new DashboardUIOptions());
        ctx.Services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(new Microsoft.AspNetCore.Http.HttpContextAccessor());
        ctx.Services.AddScoped<AuditActorAccessor>();
        ctx.Services.AddScoped(sp => new AuditLogService(
            _storage,
            sp.GetRequiredService<DashboardUIOptions>(),
            sp.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>(),
            sp.GetService<AuditActorAccessor>()));
        ctx.Services.AddScoped(sp => new HangfireMonitorService(_storage, sp.GetRequiredService<AuditLogService>()));
        ctx.Services.AddScoped(sp => new HealthCheckService(
            sp.GetRequiredService<HangfireMonitorService>(), sp.GetRequiredService<DashboardUIOptions>(), null));
        ctx.Services.AddSingleton<HealthReportCache>();
        ctx.Services.AddSingleton<DashboardSubscriptionTracker>();
        ctx.Services.AddSingleton<DashboardMetricsFeed>();

        var feed = ctx.Services.GetRequiredService<DashboardMetricsFeed>();
        var tracker = ctx.Services.GetRequiredService<DashboardSubscriptionTracker>();

        var cut = ctx.Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("Detailed metrics"), TestTimeouts.RenderWait);
        cut.WaitForState(() => feed.MetricsSubscriberCount == 1, TestTimeouts.RenderWait);
        Assert.True(tracker.HasMetricsSubscribers);

        // Loose JS interop restores the "expanded" preference as false, so open the stat grid.
        cut.WaitForState(() => !cut.Markup.Contains("hf-stats-toggle--expanded"), TestTimeouts.RenderWait);
        cut.Find(".hf-stats-toggle").Click();
        cut.WaitForState(() => cut.Markup.Contains("hf-detailed-metrics"), TestTimeouts.RenderWait);

        // The page renders the pushed payload without reading storage itself.
        feed.PublishMetrics(new DashboardMetricsSnapshot
        {
            Statistics = new StatisticsDto { Enqueued = 4242 },
            Servers = [],
            CapturedAtUtc = DateTime.UtcNow,
        });
        cut.WaitForState(() => cut.Markup.Contains("4242"), TestTimeouts.RenderWait);

        await ctx.DisposeAsync();

        Assert.Equal(0, feed.MetricsSubscriberCount);
        Assert.False(tracker.HasMetricsSubscribers);
    }
}
