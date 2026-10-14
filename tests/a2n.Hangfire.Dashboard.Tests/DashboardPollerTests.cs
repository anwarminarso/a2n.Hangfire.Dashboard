using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// <see cref="DashboardPoller"/>, the refresh loop that replaces the pages' overlapping
/// <see cref="System.Threading.Timer"/> callbacks (issue #64). Time is a <see cref="FakeTimeProvider"/>, and every
/// delay the loop waits for is reported through <see cref="DashboardPoller.DelayScheduled"/>, so the
/// tests advance time by exactly that delay.
/// </summary>
public sealed class DashboardPollerTests : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _time = new();
    private readonly Channel<TimeSpan> _delays = Channel.CreateUnbounded<TimeSpan>();
    private readonly Channel<int> _ticks = Channel.CreateUnbounded<int>();
    private readonly CapturingLogger _logger = new();
    private readonly PollingOptions _options = new()
    {
        MaxBackoffInterval = TimeSpan.FromSeconds(20),
        SlowCallThreshold = TimeSpan.FromSeconds(5),
    };
    private int _tickCount;
    private DashboardPoller _poller;

    public void Dispose() => _poller?.Dispose();

    private DashboardPoller Create(Func<CancellationToken, Task> body, TimeSpan? interval = null,
        DashboardCircuitState circuit = null, TimeSpan? initialDelay = null, bool fixedCadence = false,
        bool stopOnObjectDisposed = true)
    {
        _poller = new DashboardPoller("test", interval ?? Interval, async ct =>
        {
            // Off the thread that advanced the fake clock, as a storage call would be.
            await Task.Yield();
            var n = Interlocked.Increment(ref _tickCount);
            _ticks.Writer.TryWrite(n);
            await body(ct);
        }, _options, circuit, _logger, _time)
        {
            DelayScheduled = d => _delays.Writer.TryWrite(d),
            InitialDelay = initialDelay,
            FixedCadence = fixedCadence,
            StopOnObjectDisposed = stopOnObjectDisposed,
        };
        return _poller;
    }

    private async Task<TimeSpan> NextDelay()
    {
        using var cts = new CancellationTokenSource(Wait);
        return await _delays.Reader.ReadAsync(cts.Token);
    }

    private async Task NextTick()
    {
        using var cts = new CancellationTokenSource(Wait);
        await _ticks.Reader.ReadAsync(cts.Token);
    }

    [Fact]
    public async Task TheFirstTick_RunsOneIntervalAfterStart()
    {
        Create(_ => Task.CompletedTask).Start();

        Assert.Equal(Interval, await NextDelay());
        Assert.Equal(0, _tickCount);

        _time.Advance(Interval);
        await NextTick();
        Assert.Equal(Interval, await NextDelay());
    }

    [Fact]
    public async Task ATickInProgress_IsNeverOverlapped()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Create(_ => release.Task).Start();
        _time.Advance(await NextDelay());
        await NextTick();

        // A Timer would have fired ten more callbacks by now.
        _time.Advance(Interval * 10);
        await Task.Delay(100);
        Assert.Equal(1, _tickCount);
        Assert.False(_delays.Reader.TryRead(out _));

        // The next tick is scheduled only now. That tick took 30 s, so it also backs off.
        release.SetResult();
        Assert.Equal(Interval * 2, await NextDelay());
        Assert.Equal(1, _tickCount);
    }

    [Fact]
    public async Task FailingTicks_BackOffUpToTheCap_AndRecoverOnSuccess()
    {
        var fail = true;
        Create(_ => fail ? Task.FromException(new InvalidOperationException("storage down")) : Task.CompletedTask).Start();

        // 3 s, then doubling: 6, 12, 24 capped at 20, 20.
        foreach (var expected in new[] { 3, 6, 12, 20, 20 })
        {
            var delay = await NextDelay();
            Assert.Equal(TimeSpan.FromSeconds(expected), delay);
            _time.Advance(delay);
        }

        fail = false;
        _time.Advance(await NextDelay());
        Assert.Equal(Interval, await NextDelay());
        Assert.Equal(Interval, _poller.CurrentDelay);

        // One warning going into back-off and one message coming out, not one per tick.
        Assert.Equal(1, _logger.Count(LogLevel.Warning));
        Assert.Equal(1, _logger.Count(LogLevel.Information));
    }

    [Fact]
    public async Task ASlowTick_BacksOffLikeAFailedOne()
    {
        var slow = true;
        Create(_ =>
        {
            if (slow) _time.Advance(TimeSpan.FromSeconds(6));
            return Task.CompletedTask;
        }).Start();

        _time.Advance(await NextDelay());
        Assert.Equal(Interval * 2, await NextDelay());
        Assert.Equal(1, _logger.Count(LogLevel.Warning));

        slow = false;
        _time.Advance(Interval * 2);
        Assert.Equal(Interval, await NextDelay());
    }

    [Fact]
    public async Task ALongInterval_IsNotSlowedFurther()
    {
        Create(_ => throw new InvalidOperationException(), interval: TimeSpan.FromMinutes(2)).Start();

        _time.Advance(await NextDelay());
        Assert.Equal(TimeSpan.FromMinutes(2), await NextDelay());
    }

    [Fact]
    public async Task WhileTheCircuitIsDisconnected_NoTickRuns()
    {
        var circuit = new DashboardCircuitState();
        circuit.SetConnected(false);
        Create(_ => Task.CompletedTask, circuit: circuit).Start();

        _time.Advance(await NextDelay());
        _time.Advance(Interval * 10);
        await Task.Delay(100);
        Assert.Equal(0, _tickCount);

        // On reconnect the page is refreshed at once, since what it shows is stale.
        circuit.SetConnected(true);
        await NextTick();
    }

    [Fact]
    public async Task Dispose_StopsTheLoop_AndCancelsTheTickInProgress()
    {
        var cancelled = false;
        Create(async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled = true; throw; }
        }).Start();
        _time.Advance(await NextDelay());
        await NextTick();

        _poller.Dispose();
        await _poller.Completion.WaitAsync(Wait);

        Assert.True(cancelled);
        Assert.False(_delays.Reader.TryRead(out _));
        Assert.Equal(0, _logger.Count(LogLevel.Warning));
    }

    [Fact]
    public async Task Dispose_BeforeTheFirstTick_RunsNone()
    {
        Create(_ => Task.CompletedTask).Start();
        await NextDelay();

        _poller.Dispose();
        await _poller.Completion.WaitAsync(Wait);
        _time.Advance(Interval * 5);

        Assert.Equal(0, _tickCount);
    }

    [Fact]
    public async Task AnInterval_OfZero_DisablesTheLoop()
    {
        var poller = Create(_ => Task.CompletedTask, interval: TimeSpan.Zero);
        poller.Start();

        Assert.False(poller.IsEnabled);
        await poller.Completion.WaitAsync(Wait);
        Assert.False(_delays.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ARendererBeingDisposed_StopsTheLoopQuietly()
    {
        Create(_ => throw new ObjectDisposedException("renderer")).Start();
        _time.Advance(await NextDelay());

        await _poller.Completion.WaitAsync(Wait);
        Assert.Equal(0, _logger.Count(LogLevel.Warning));
    }

    [Fact]
    public async Task AnInitialDelay_ReplacesTheFirstInterval()
    {
        Create(_ => Task.CompletedTask, initialDelay: TimeSpan.Zero).Start();

        // No clock advance needed: the first tick runs as soon as the loop starts.
        Assert.Equal(TimeSpan.Zero, await NextDelay());
        await NextTick();
        Assert.Equal(Interval, await NextDelay());
    }

    [Fact]
    public async Task AFixedCadence_SubtractsTheTicksDuration()
    {
        Create(_ =>
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            return Task.CompletedTask;
        }, fixedCadence: true).Start();

        _time.Advance(await NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(2), await NextDelay());
        Assert.Equal(0, _logger.Count(LogLevel.Warning));
    }

    [Fact]
    public async Task AFixedCadence_BacksOff_WhenATickTakesTheWholeInterval()
    {
        // 3 s is below the 5 s slow threshold, but a fixed-cadence loop would otherwise start the
        // next tick at once and keep the storage busy back to back.
        var slow = true;
        Create(_ =>
        {
            if (slow) _time.Advance(Interval);
            return Task.CompletedTask;
        }, fixedCadence: true).Start();

        _time.Advance(await NextDelay());
        Assert.Equal(Interval * 2, await NextDelay());
        Assert.Equal(1, _logger.Count(LogLevel.Warning));

        slow = false;
        _time.Advance(Interval * 2);
        Assert.Equal(Interval, await NextDelay());
        Assert.Equal(1, _logger.Count(LogLevel.Information));
    }

    [Fact]
    public async Task ABackgroundLoop_BacksOffOnObjectDisposed_InsteadOfStopping()
    {
        Create(_ => throw new ObjectDisposedException("connection"), stopOnObjectDisposed: false).Start();

        _time.Advance(await NextDelay());
        Assert.Equal(Interval * 2, await NextDelay());
        Assert.False(_poller.Completion.IsCompleted);
        Assert.Equal(1, _logger.Count(LogLevel.Warning));
    }

    [Fact]
    public async Task RunUntilStopped_EndsWhenTheTokenIsCancelled()
    {
        var poller = Create(_ => Task.CompletedTask);
        using var stopping = new CancellationTokenSource();

        var run = poller.RunUntilStoppedAsync(stopping.Token);
        await NextDelay();
        Assert.False(run.IsCompleted);

        stopping.Cancel();
        await run.WaitAsync(Wait);
        Assert.Equal(0, _tickCount);
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly ConcurrentQueue<LogLevel> _levels = new();

        public int Count(LogLevel level)
        {
            var n = 0;
            foreach (var l in _levels) if (l == level) n++;
            return n;
        }

        public IDisposable BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter) => _levels.Enqueue(logLevel);
    }
}
