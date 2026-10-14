namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Bounds how many of the dashboard's synchronous storage calls run at the same time, process-wide
/// (issue #64).
/// </summary>
/// <remarks>
/// <para>
/// Hangfire's monitoring API and storage connections are synchronous, so each dashboard read holds
/// a thread-pool thread until storage answers. With a slow storage (for example a PostgreSQL pool
/// that is exhausted, where every call waits up to the pool timeout), page refreshes from many open
/// tabs used to pile up blocked threads without limit, until the host's thread pool starved.
/// </para>
/// <para>
/// Every call goes through one <see cref="SemaphoreSlim"/> sized by
/// <see cref="PollingOptions.MaxConcurrentStorageCalls"/>. A caller waiting for a slot holds no
/// thread. A call keeps its slot until it has actually returned, even when its caller stopped
/// waiting (the page was closed, or a timeout fired), so at most <see cref="Limit"/> threads are
/// ever blocked in storage on the dashboard's behalf.
/// </para>
/// </remarks>
public sealed class DashboardStorageGate
{
    private readonly DashboardUIOptions _options;
    private readonly object _initLock = new();
    private SemaphoreSlim _semaphore;
    private int _limit;
    private int _inFlight;

    public DashboardStorageGate(DashboardUIOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Maximum number of calls in flight at once, from
    /// <see cref="PollingOptions.MaxConcurrentStorageCalls"/> (at least 1). Fixed on first use, since
    /// the host's options are applied after the services are registered.
    /// </summary>
    public int Limit
    {
        get
        {
            EnsureSemaphore();
            return _limit;
        }
    }

    /// <summary>Number of calls currently running in storage (diagnostics and tests).</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>
    /// Runs <paramref name="call"/> on the thread pool once a slot is free and returns its result.
    /// </summary>
    /// <param name="call">The synchronous storage call.</param>
    /// <param name="ct">
    /// Stops waiting for a slot, or for the result. A call that has already started can't be
    /// cancelled; it keeps its slot until it returns.
    /// </param>
    public async Task<T> RunAsync<T>(Func<T> call, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        var semaphore = EnsureSemaphore();

        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref _inFlight);
        var task = Task.Run(call, CancellationToken.None);

        // The slot is released when the call returns, not when the caller stops waiting.
        _ = task.ContinueWith(
            static (_, state) => ((DashboardStorageGate)state).Release(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return await task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="call"/> on the thread pool once a slot is free.
    /// </summary>
    /// <inheritdoc cref="RunAsync{T}(Func{T}, CancellationToken)"/>
    public Task RunAsync(Action call, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        return RunAsync<object>(() =>
        {
            call();
            return null;
        }, ct);
    }

    private void Release()
    {
        Interlocked.Decrement(ref _inFlight);
        _semaphore.Release();
    }

    private SemaphoreSlim EnsureSemaphore()
    {
        var semaphore = Volatile.Read(ref _semaphore);
        if (semaphore is not null) return semaphore;

        lock (_initLock)
        {
            if (_semaphore is null)
            {
                var limit = Math.Max(1, _options.Polling?.MaxConcurrentStorageCalls
                    ?? PollingOptions.DefaultMaxConcurrentStorageCalls);
                _limit = limit;
                Volatile.Write(ref _semaphore, new SemaphoreSlim(limit, limit));
            }
            return _semaphore;
        }
    }
}
