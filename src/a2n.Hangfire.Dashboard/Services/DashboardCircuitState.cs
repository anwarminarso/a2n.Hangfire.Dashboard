using Microsoft.AspNetCore.Components.Server.Circuits;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Whether the current Blazor circuit has a live connection to its browser, so the dashboard's
/// refresh loops can pause while nobody can see their results (issue #64).
/// </summary>
/// <remarks>
/// <para>
/// Blazor keeps a circuit alive for <c>DisconnectedCircuitRetentionPeriod</c> (3 minutes by default)
/// after its connection drops, so the user can reconnect. Its components, and their refresh loops,
/// stay alive with it. Without this, a "reconnecting" storm kept every retained circuit reading
/// storage on top of the new ones.
/// </para>
/// <para>
/// Registered per scope (one per circuit) and as a <see cref="CircuitHandler"/>. Outside a circuit
/// (the prerender pass, or a plain HTTP request) no connection events arrive and
/// <see cref="IsConnected"/> stays <c>true</c>.
/// </para>
/// </remarks>
public sealed class DashboardCircuitState : CircuitHandler
{
    private readonly object _lock = new();
    private TaskCompletionSource _connected = NewConnectedSource(completed: true);

    /// <summary>True unless the circuit's connection is currently down.</summary>
    public bool IsConnected
    {
        get
        {
            lock (_lock) return _connected.Task.IsCompleted;
        }
    }

    /// <summary>Completes when the circuit is connected: at once, or when its connection comes back.</summary>
    public Task WaitForConnectedAsync(CancellationToken ct = default)
    {
        Task connected;
        lock (_lock) connected = _connected.Task;
        return connected.IsCompleted ? Task.CompletedTask : connected.WaitAsync(ct);
    }

    /// <inheritdoc />
    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        SetConnected(true);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        SetConnected(false);
        return Task.CompletedTask;
    }

    internal void SetConnected(bool connected)
    {
        TaskCompletionSource toComplete = null;
        lock (_lock)
        {
            if (connected)
            {
                toComplete = _connected;
            }
            else if (_connected.Task.IsCompleted)
            {
                _connected = NewConnectedSource(completed: false);
            }
        }
        toComplete?.TrySetResult();
    }

    private static TaskCompletionSource NewConnectedSource(bool completed)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed) source.SetResult();
        return source;
    }
}
