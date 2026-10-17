using a2n.Hangfire.Dashboard.Security;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Holds the <see cref="DashboardPermissions"/> of the current scope: the HTTP request during the
/// static render and prerender, or the circuit once it is interactive.
/// </summary>
/// <remarks>
/// <para>
/// The first value set wins, so nothing rendered later in the scope can widen the permissions.
/// </para>
/// <para>
/// Inside the dashboard, <c>DashboardApp</c> (request scope) and <c>DashboardRoutes</c> (circuit
/// scope) always set a value before any page runs, failing closed when the permissions are missing.
/// A scope that never renders the dashboard (host code resolving the services itself) keeps the
/// <see cref="DashboardUIOptions.IsReadOnly"/> behavior.
/// </para>
/// </remarks>
internal sealed class DashboardPermissionsState
{
    private readonly DashboardUIOptions _options;
    private DashboardPermissions _current;

    public DashboardPermissionsState(DashboardUIOptions options = null)
    {
        _options = options;
    }

    /// <summary>The permissions of this scope; <see cref="DashboardPermissions.FromOptions"/> until set.</summary>
    public DashboardPermissions Current => Volatile.Read(ref _current) ?? DashboardPermissions.FromOptions(_options);

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
