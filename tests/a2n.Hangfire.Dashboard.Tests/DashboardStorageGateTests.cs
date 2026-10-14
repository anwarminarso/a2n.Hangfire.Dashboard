using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// <see cref="DashboardStorageGate"/>, the process-wide limit on the dashboard's synchronous storage
/// calls (issue #64). The calls here really block a thread, like a storage call waiting for a
/// pooled connection.
/// </summary>
public class DashboardStorageGateTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static DashboardStorageGate Gate(int limit)
        => new(new DashboardUIOptions { Polling = new PollingOptions { MaxConcurrentStorageCalls = limit } });

    [Fact]
    public async Task NoMoreThanTheLimit_RunAtOnce()
    {
        var gate = Gate(2);
        using var release = new ManualResetEventSlim();
        var running = 0;
        var maxRunning = 0;

        var calls = Enumerable.Range(0, 10).Select(i => gate.RunAsync(() =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxRunning, now);
            release.Wait(Wait);
            Interlocked.Decrement(ref running);
            return i;
        })).ToArray();

        await WaitUntil(() => Volatile.Read(ref running) == 2);
        await Task.Delay(100);
        Assert.Equal(2, Volatile.Read(ref running));
        Assert.Equal(2, gate.InFlight);

        release.Set();
        var results = await Task.WhenAll(calls).WaitAsync(Wait);

        Assert.Equal(Enumerable.Range(0, 10), results);
        Assert.Equal(2, maxRunning);
        Assert.Equal(0, gate.InFlight);
    }

    [Fact]
    public async Task ACallerThatStopsWaiting_KeepsTheSlotUntilTheCallReturns()
    {
        // An abandoned call still blocks a thread in storage, so it still counts.
        var gate = Gate(1);
        using var releaseFirst = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = gate.RunAsync(() =>
        {
            firstStarted.SetResult();
            releaseFirst.Wait(Wait);
            return 1;
        }, cts.Token);
        await firstStarted.Task.WaitAsync(Wait);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var secondStarted = false;
        var second = gate.RunAsync(() => secondStarted = true);
        await Task.Delay(100);
        Assert.False(secondStarted);
        Assert.Equal(1, gate.InFlight);

        releaseFirst.Set();
        await second.WaitAsync(Wait);
        Assert.True(secondStarted);
    }

    [Fact]
    public async Task ACallerWaitingForASlot_CanStopWaiting_WithoutRunningTheCall()
    {
        var gate = Gate(1);
        using var release = new ManualResetEventSlim();
        var busy = gate.RunAsync(() => release.Wait(Wait));
        await WaitUntil(() => gate.InFlight == 1);

        using var cts = new CancellationTokenSource();
        var ran = false;
        var waiting = gate.RunAsync(() => ran = true, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        release.Set();
        await busy.WaitAsync(Wait);
        Assert.False(ran);
        Assert.Equal(0, gate.InFlight);
    }

    [Fact]
    public async Task AFailingCall_Throws_AndFreesItsSlot()
    {
        var gate = Gate(1);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gate.RunAsync<int>(() => throw new InvalidOperationException("storage error")));

        Assert.Equal(7, await gate.RunAsync(() => 7).WaitAsync(Wait));
        Assert.Equal(0, gate.InFlight);
    }

    [Theory]
    [InlineData(4, 4)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    public void TheLimit_ComesFromTheOptions_AndIsAtLeastOne(int configured, int expected)
    {
        Assert.Equal(expected, Gate(configured).Limit);
    }

    [Fact]
    public void WithoutPollingOptions_TheDefaultLimitApplies()
    {
        var gate = new DashboardStorageGate(new DashboardUIOptions { Polling = null });

        Assert.Equal(PollingOptions.DefaultMaxConcurrentStorageCalls, gate.Limit);
    }

    [Fact]
    public void TheLimit_IsReadOnFirstUse_AfterTheHostsOptionsAreApplied()
    {
        // AddHangfireDashboardUI registers the options singleton; UseHangfireDashboardUI copies the
        // host's values onto it later.
        var registered = new DashboardUIOptions();
        var gate = new DashboardStorageGate(registered);
        new DashboardUIOptions { Polling = new PollingOptions { MaxConcurrentStorageCalls = 3 } }.ApplyTo(registered);

        Assert.Equal(3, gate.Limit);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target))
               && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not reached.");
            await Task.Delay(10);
        }
    }
}
