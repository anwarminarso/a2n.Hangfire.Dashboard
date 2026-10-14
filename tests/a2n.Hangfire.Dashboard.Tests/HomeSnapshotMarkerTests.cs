using System;
using Bunit;
using Hangfire.InMemory;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Components.Pages;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// The Home page exposes when the metrics it shows were read, so the load-test harness can prove
/// realtime updates reach the page from the DOM, even when no count changes between updates.
/// </summary>
public class HomeSnapshotMarkerTests
{
    [Fact]
    public void TheMarker_FollowsEachRealtimeSnapshot()
    {
        var storage = new InMemoryStorage();
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var options = new DashboardUIOptions();
        ctx.Services.AddSingleton(options);
        ctx.Services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(new Microsoft.AspNetCore.Http.HttpContextAccessor());
        ctx.Services.AddScoped<AuditActorAccessor>();
        ctx.Services.AddScoped(sp => new AuditLogService(storage, options,
            sp.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>(), sp.GetService<AuditActorAccessor>()));
        ctx.Services.AddScoped(sp => new HangfireMonitorService(storage, sp.GetRequiredService<AuditLogService>()));
        ctx.Services.AddScoped(sp => new HealthCheckService(sp.GetRequiredService<HangfireMonitorService>(), options, null));
        ctx.Services.AddSingleton<HealthReportCache>();
        ctx.Services.AddDashboardPolling();

        var cut = ctx.Render<Home>();
        cut.WaitForState(() => Marker(cut) != "0", TestTimeouts.RenderWait);

        var captured = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        ctx.Services.GetRequiredService<DashboardMetricsFeed>().PublishMetrics(new DashboardMetricsSnapshot
        {
            Statistics = new StatisticsDto(),
            CapturedAtUtc = captured,
        });

        cut.WaitForAssertion(() => Assert.Equal(captured.Ticks.ToString(), Marker(cut)), TestTimeouts.RenderWait);
    }

    private static string Marker(IRenderedComponent<Home> cut)
        => cut.Find("[data-testid=metrics-snapshot]").GetAttribute("data-snapshot-at");
}
