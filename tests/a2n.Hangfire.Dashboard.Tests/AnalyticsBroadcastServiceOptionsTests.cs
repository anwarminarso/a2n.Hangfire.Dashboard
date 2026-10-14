using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// <see cref="PollingOptions.AnalyticsBroadcastInterval"/> (issue #64): the analytics push used a
/// hardcoded 5-second cadence that could not be changed or turned off.
/// </summary>
public class AnalyticsBroadcastServiceOptionsTests
{
    private static AnalyticsBroadcastService Create(DashboardUIOptions options) =>
        new(new ServiceCollection().BuildServiceProvider(), hubContext: null,
            subscriptions: new DashboardSubscriptionTracker(), feed: null, options,
            NullLogger<AnalyticsBroadcastService>.Instance);

    [Fact]
    public async Task AZeroInterval_DisablesTheBroadcast()
    {
        var options = new DashboardUIOptions();
        options.Polling.AnalyticsBroadcastInterval = TimeSpan.Zero;
        using var service = Create(options);

        await service.StartAsync(CancellationToken.None);

        Assert.NotNull(service.ExecuteTask);
        await service.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task APositiveInterval_KeepsTheBroadcastRunning_UntilStopped()
    {
        using var service = Create(new DashboardUIOptions());

        await service.StartAsync(CancellationToken.None);
        Assert.False(service.ExecuteTask.IsCompleted);

        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(service.ExecuteTask.IsCompleted);
    }
}
