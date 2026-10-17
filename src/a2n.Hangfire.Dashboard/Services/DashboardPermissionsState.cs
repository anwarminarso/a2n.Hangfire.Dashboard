using a2n.Hangfire.Dashboard.Security;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Holds the <see cref="DashboardPermissions"/> of the current scope: the HTTP request during the
/// static render and prerender, or the circuit once it is interactive.
/// </summary>
/// <remarks>
/// The first value set wins, so nothing rendered later in the scope can widen the permissions.
/// Until a value is set the scope is treated as read-only.
/// </remarks>
internal sealed class DashboardPermissionsState
{
    private DashboardPermissions _current;

    /// <summary>The permissions of this scope, or <see cref="DashboardPermissions.FailedClosed"/> until set.</summary>
    public DashboardPermissions Current => Volatile.Read(ref _current) ?? DashboardPermissions.FailedClosed;

    /// <summary>Whether a value has been set for this scope.</summary>
    public bool IsInitialized => Volatile.Read(ref _current) is not null;

    /// <summary>Whether write actions are refused in this scope.</summary>
    public bool IsReadOnly => Current.IsReadOnly;

    /// <summary>Sets the permissions of this scope unless they were already set.</summary>
    public void Initialize(DashboardPermissions permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        Interlocked.CompareExchange(ref _current, permissions, null);
    }
}
