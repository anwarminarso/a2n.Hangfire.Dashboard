using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// What a dashboard component needs to read storage without starving the thread pool (issue #64):
/// the process-wide <see cref="DashboardStorageGate"/> for its reads, and refresh loops
/// (<see cref="DashboardPoller"/>) that pause with the circuit and back off while storage is slow.
/// One per scope, so per circuit.
/// </summary>
internal sealed class DashboardPolling
{
    private readonly DashboardStorageGate _gate;
    private readonly DashboardUIOptions _options;
    private readonly DashboardCircuitState _circuit;
    private readonly ILoggerFactory _loggerFactory;

    public DashboardPolling(
        DashboardStorageGate gate,
        DashboardUIOptions options,
        DashboardCircuitState circuit = null,
        ILoggerFactory loggerFactory = null)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _circuit = circuit;
        _loggerFactory = loggerFactory;
    }

    /// <summary>Resolves the optional collaborators, so a host or test without logging still works.</summary>
    internal static DashboardPolling Create(IServiceProvider services) => new(
        services.GetRequiredService<DashboardStorageGate>(),
        services.GetRequiredService<DashboardUIOptions>(),
        services.GetService<DashboardCircuitState>(),
        services.GetService<ILoggerFactory>());

    /// <summary>The host's polling options.</summary>
    public PollingOptions Options => _options.Polling ?? new PollingOptions();

    /// <summary>Runs a synchronous storage read through the <see cref="DashboardStorageGate"/>.</summary>
    public Task<T> ReadAsync<T>(Func<T> read, CancellationToken ct = default) => _gate.RunAsync(read, ct);

    /// <summary>Runs a synchronous storage read through the <see cref="DashboardStorageGate"/>.</summary>
    public Task ReadAsync(Action read, CancellationToken ct = default) => _gate.RunAsync(read, ct);

    /// <summary>
    /// Starts a refresh loop. Dispose the returned poller when the component is disposed. An
    /// interval of zero or less returns a poller that never ticks.
    /// </summary>
    /// <param name="name">Name used in log messages, typically the component's.</param>
    /// <param name="interval">Delay between the end of one refresh and the start of the next.</param>
    /// <param name="tick">The refresh; its token is cancelled when the poller is disposed.</param>
    public DashboardPoller Start(string name, TimeSpan interval, Func<CancellationToken, Task> tick)
    {
        var poller = new DashboardPoller(name, interval, tick, Options, _circuit,
            _loggerFactory?.CreateLogger($"{typeof(DashboardPoller).Namespace}.Polling.{name}"));
        poller.Start();
        return poller;
    }
}
