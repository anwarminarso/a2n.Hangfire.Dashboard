using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// <see cref="DashboardCircuitState"/>, which lets refresh loops pause while their circuit's
/// connection is down (issue #64).
/// </summary>
public class DashboardCircuitStateTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task OutsideACircuit_ItCountsAsConnected()
    {
        // The prerender pass and plain HTTP requests get no connection events.
        var state = new DashboardCircuitState();

        Assert.True(state.IsConnected);
        await state.WaitForConnectedAsync().WaitAsync(Wait);
    }

    [Fact]
    public async Task WhenTheConnectionDrops_WaitersResumeOnceItComesBack()
    {
        var state = new DashboardCircuitState();

        await state.OnConnectionDownAsync(null, CancellationToken.None);
        Assert.False(state.IsConnected);
        var waiting = state.WaitForConnectedAsync();
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);

        await state.OnConnectionUpAsync(null, CancellationToken.None);
        await waiting.WaitAsync(Wait);
        Assert.True(state.IsConnected);
    }

    [Fact]
    public async Task AWaiter_CanStopWaiting()
    {
        var state = new DashboardCircuitState();
        state.SetConnected(false);
        using var cts = new CancellationTokenSource();

        var waiting = state.WaitForConnectedAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public void ItIsRegistered_AsTheCircuitsHandler_OnePerScope()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<JobStorage>(new InMemoryStorage());
        services.AddHangfireDashboardUI();
        using var provider = services.BuildServiceProvider();

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var state = first.ServiceProvider.GetRequiredService<DashboardCircuitState>();

        Assert.Contains(first.ServiceProvider.GetServices<CircuitHandler>(), h => ReferenceEquals(h, state));
        Assert.NotSame(state, second.ServiceProvider.GetRequiredService<DashboardCircuitState>());
        Assert.Same(provider.GetRequiredService<DashboardStorageGate>(), first.ServiceProvider.GetRequiredService<DashboardStorageGate>());
    }
}
