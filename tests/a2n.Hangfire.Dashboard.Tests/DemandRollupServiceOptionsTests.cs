using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Tests for the switches that stop <see cref="DemandRollupService"/> from polling (issue #61:
/// the 60-second poll ran even with nobody watching and could not be turned off).
/// </summary>
public class DemandRollupServiceOptionsTests
{
    private static (DemandRollupService Service, DashboardUIOptions Options) Create()
    {
        var options = new DashboardUIOptions();
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton<JobStorage>(new InMemoryStorage());
        services.AddSingleton(Mock.Of<IStorageMetricsProvider>());
        var sp = services.BuildServiceProvider();
        return (new DemandRollupService(sp), options);
    }

    [Fact]
    public void Enabled_ByDefault()
    {
        var (service, _) = Create();

        Assert.True(service.IsAvailable);
        Assert.True(service.IsEnabled);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Disabled_WhenHeatmapOrRollupIsOff(bool heatmapEnabled, bool rollupEnabled)
    {
        var (service, options) = Create();

        // Mutated after construction, as UseHangfireDashboardUI does via ApplyTo.
        options.Heatmap.Enabled = heatmapEnabled;
        options.Heatmap.DemandRollupEnabled = rollupEnabled;

        Assert.False(service.IsEnabled);
    }

    [Fact]
    public void HostOptions_AppliedAfterConstruction_AreHonored()
    {
        var (service, registered) = Create();
        var host = new DashboardUIOptions { Heatmap = new HeatmapOptions { DemandRollupEnabled = false } };

        host.ApplyTo(registered);

        Assert.False(service.IsEnabled);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Start_RunsTheLoop_OnlyWhenEnabled(bool rollupEnabled, bool expectRunning)
    {
        var (service, options) = Create();
        options.Heatmap.DemandRollupEnabled = rollupEnabled;

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(expectRunning, service.IsRunning);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).WaitAsync(System.TimeSpan.FromSeconds(10));
            service.Dispose();
        }
    }

    [Fact]
    public async Task Start_DoesNotRunTheLoop_WhenThePollIntervalIsZero()
    {
        var (service, options) = Create();
        options.Polling.RollupPollInterval = System.TimeSpan.Zero;

        await service.StartAsync(CancellationToken.None);

        Assert.True(service.IsEnabled);
        Assert.False(service.IsRunning);
        await service.StopAsync(CancellationToken.None);
        service.Dispose();
    }
}
