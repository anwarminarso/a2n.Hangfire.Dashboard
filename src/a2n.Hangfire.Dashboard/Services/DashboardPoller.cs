using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// A component's refresh loop: runs a tick every interval, never two at once, backs off while
/// ticks fail or are slow, and pauses while the circuit is disconnected (issue #64).
/// </summary>
/// <remarks>
/// <para>
/// It replaces the pages' <see cref="Timer"/> callbacks. A <see cref="Timer"/> fires every period
/// whether or not the previous callback has returned, so with a slow storage each open tab kept
/// adding blocked threads. Here the next tick is scheduled only after the previous one has
/// finished: one tick in flight per loop, whatever the storage does.
/// </para>
/// <para>
/// A tick that throws, or that takes at least <see cref="PollingOptions.SlowCallThreshold"/>,
/// doubles the delay before the next one, up to <see cref="PollingOptions.MaxBackoffInterval"/>.
/// The first tick that succeeds in time restores the normal interval. The change into and out of
/// that state is logged once, not on every tick.
/// </para>
/// <para>
/// The first tick runs one interval after <see cref="Start"/> (or after <see cref="InitialDelay"/>);
/// components load their data themselves before starting the loop. An interval of zero or less
/// disables the loop.
/// </para>
/// <para>
/// By default the interval is the pause between the end of one tick and the start of the next.
/// With <see cref="FixedCadence"/> it is the period from the start of one tick to the start of the
/// next, as the background broadcasts and rollups had with <see cref="PeriodicTimer"/>; a tick that
/// takes the whole interval then counts as slow, so ticks never run back to back.
/// </para>
/// </remarks>
internal sealed class DashboardPoller : IDisposable
{
    private readonly string _name;
    private readonly TimeSpan _interval;
    private readonly Func<CancellationToken, Task> _tick;
    private readonly PollingOptions _options;
    private readonly DashboardCircuitState _circuit;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private Task _loop;
    private int _started;
    private int _disposed;
    private long _currentDelayTicks;
    private bool _degraded;

    /// <param name="name">Name used in log messages, typically the component's.</param>
    /// <param name="interval">Normal delay between the end of one tick and the start of the next.</param>
    /// <param name="tick">The refresh. Receives a token that is cancelled when the loop is disposed.</param>
    /// <param name="options">Back-off settings.</param>
    /// <param name="circuit">When given, ticks wait while the circuit is disconnected.</param>
    /// <param name="logger">Receives one message per change into or out of back-off.</param>
    /// <param name="timeProvider">Time source; <see cref="TimeProvider.System"/> by default.</param>
    public DashboardPoller(
        string name,
        TimeSpan interval,
        Func<CancellationToken, Task> tick,
        PollingOptions options,
        DashboardCircuitState circuit = null,
        ILogger logger = null,
        TimeProvider timeProvider = null)
    {
        _name = name ?? "refresh";
        _interval = interval;
        _tick = tick ?? throw new ArgumentNullException(nameof(tick));
        _options = options ?? new PollingOptions();
        _circuit = circuit;
        _logger = logger ?? NullLogger.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _currentDelayTicks = interval.Ticks;
    }

    /// <summary>False when the interval is zero or less, so <see cref="Start"/> does nothing.</summary>
    public bool IsEnabled => _interval > TimeSpan.Zero;

    /// <summary>The delay before the next tick: the interval, or longer while backing off.</summary>
    public TimeSpan CurrentDelay => TimeSpan.FromTicks(Volatile.Read(ref _currentDelayTicks));

    /// <summary>
    /// Delay before the first tick. <c>null</c> (the default) waits one interval; zero runs the
    /// first tick as soon as the loop starts.
    /// </summary>
    public TimeSpan? InitialDelay { get; init; }

    /// <summary>
    /// When true the interval is measured from the start of one tick to the start of the next
    /// rather than from the end of one to the start of the next, and a tick that takes the whole
    /// interval counts as slow. Default: false.
    /// </summary>
    public bool FixedCadence { get; init; }

    /// <summary>
    /// How long a tick may take before it counts as slow. <c>null</c> (the default) uses
    /// <see cref="PollingOptions.SlowCallThreshold"/>; zero or less never counts a tick as slow,
    /// except for <see cref="FixedCadence"/> loops, which always treat a tick as slow once it takes
    /// the whole interval.
    /// </summary>
    public TimeSpan? SlowThreshold { get; init; }

    /// <summary>
    /// When true (the default) a tick that throws <see cref="ObjectDisposedException"/> ends the
    /// loop quietly: for a component that means its renderer is being torn down. Background loops
    /// turn it off so the exception backs the loop off like any other failure.
    /// </summary>
    public bool StopOnObjectDisposed { get; init; } = true;

    /// <summary>The running loop; completes once the poller is disposed.</summary>
    internal Task Completion => _loop ?? Task.CompletedTask;

    /// <summary>Called with each delay just before the loop waits for it (tests).</summary>
    internal Action<TimeSpan> DelayScheduled { get; set; }

    /// <summary>Starts the loop. Does nothing when disabled, already started, or disposed.</summary>
    public void Start()
    {
        if (!IsEnabled || Volatile.Read(ref _disposed) == 1) return;
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        // On the thread pool, so a first tick with no initial delay never runs on the caller.
        var token = _cts.Token;
        _loop = Task.Run(() => RunAsync(token));
    }

    /// <summary>
    /// Starts the loop and completes once <paramref name="stoppingToken"/> is cancelled and the
    /// loop has stopped: the body of a background service.
    /// </summary>
    internal async Task RunUntilStoppedAsync(CancellationToken stoppingToken)
    {
        using (stoppingToken.Register(Dispose))
        {
            Start();
            await Completion.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops the loop. A tick in progress is told to stop through its token; the loop doesn't
    /// schedule another one.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        // Not disposed: a tick still in progress may observe or register on the token.
        _cts.Cancel();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var delay = InitialDelay is { } initial ? (initial > TimeSpan.Zero ? initial : TimeSpan.Zero) : _interval;
        Volatile.Write(ref _currentDelayTicks, delay.Ticks);
        while (true)
        {
            try
            {
                var wait = Task.Delay(delay, _time, ct);
                DelayScheduled?.Invoke(delay);
                await wait.ConfigureAwait(false);

                if (_circuit is not null && !_circuit.IsConnected)
                    await _circuit.WaitForConnectedAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var startedAt = _time.GetTimestamp();
            Exception failure = null;
            try
            {
                await _tick(ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (StopOnObjectDisposed)
            {
                // The component's renderer is being torn down with its circuit.
                return;
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (ct.IsCancellationRequested) return;

            delay = NextDelay(delay, failure, _time.GetElapsedTime(startedAt));
            Volatile.Write(ref _currentDelayTicks, delay.Ticks);
        }
    }

    private TimeSpan NextDelay(TimeSpan current, Exception failure, TimeSpan elapsed)
    {
        var slowThreshold = SlowThreshold ?? _options.SlowCallThreshold;
        if (FixedCadence && (slowThreshold <= TimeSpan.Zero || slowThreshold > _interval))
            slowThreshold = _interval;
        var slow = slowThreshold > TimeSpan.Zero && elapsed >= slowThreshold;

        if (failure is null && !slow)
        {
            if (_degraded)
            {
                _degraded = false;
                _logger.LogInformation(
                    "Dashboard refresh '{Name}' recovered; refreshing every {Interval} again.", _name, _interval);
            }
            if (!FixedCadence) return _interval;
            var remaining = _interval - elapsed;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        // Double from at least the interval: a fixed-cadence delay is shorter than the interval.
        var basis = current > _interval ? current : _interval;
        var cap = _options.MaxBackoffInterval > _interval ? _options.MaxBackoffInterval : _interval;
        var next = basis.Ticks > cap.Ticks / 2 ? cap : TimeSpan.FromTicks(basis.Ticks * 2);

        if (!_degraded)
        {
            _degraded = true;
            if (failure is not null)
            {
                _logger.LogWarning(failure,
                    "Dashboard refresh '{Name}' failed; backing off up to {MaxInterval} until it succeeds again.",
                    _name, cap);
            }
            else
            {
                _logger.LogWarning(
                    "Dashboard refresh '{Name}' took {Elapsed} (slow threshold {Threshold}); backing off up to {MaxInterval} until it is fast again.",
                    _name, elapsed, slowThreshold, cap);
            }
        }
        return next;
    }
}
