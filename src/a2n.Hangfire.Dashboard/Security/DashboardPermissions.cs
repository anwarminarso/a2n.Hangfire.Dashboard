namespace a2n.Hangfire.Dashboard.Security;

/// <summary>
/// Why the dashboard is read-only for the current page load.
/// </summary>
public enum ReadOnlyReason
{
    /// <summary>The dashboard is not read-only.</summary>
    None = 0,

    /// <summary><see cref="DashboardUIOptions.IsReadOnly"/> is set.</summary>
    Option = 1,

    /// <summary>Hangfire's <c>DashboardOptions.IsReadOnlyFunc</c> returned true for this request.</summary>
    IsReadOnlyFunc = 2,

    /// <summary>
    /// The permissions could not be determined (the read-only function threw, no request was
    /// available, or the circuit started without them), so the dashboard fails closed.
    /// </summary>
    EvaluationFailed = 3,
}

/// <summary>
/// The permissions of one page load: evaluated once from the HTTP request that renders the
/// dashboard, then handed to the interactive circuit as a root component parameter.
/// </summary>
/// <remarks>
/// Instances travel inside the server component descriptor, which ASP.NET Core protects with Data
/// Protection, so the browser can neither read nor alter them.
/// </remarks>
/// <param name="IsReadOnly">Whether write actions are refused.</param>
/// <param name="Reason">Why the dashboard is read-only; <see cref="ReadOnlyReason.None"/> when it isn't.</param>
public sealed record DashboardPermissions(bool IsReadOnly, ReadOnlyReason Reason)
{
    /// <summary>Full access.</summary>
    public static DashboardPermissions Writable { get; } = new(false, ReadOnlyReason.None);

    /// <summary>Read-only because the permissions could not be determined.</summary>
    public static DashboardPermissions FailedClosed { get; } = new(true, ReadOnlyReason.EvaluationFailed);

    internal static DashboardPermissions ReadOnlyBecause(ReadOnlyReason reason) => new(true, reason);

    /// <summary>
    /// The permissions implied by <see cref="DashboardUIOptions.IsReadOnly"/> alone: what applies
    /// outside the dashboard's own render tree (host code calling the services, component tests).
    /// Inside it, <c>DashboardRoutes</c> always supplies evaluated permissions, failing closed.
    /// </summary>
    internal static DashboardPermissions FromOptions(DashboardUIOptions options)
        => options?.IsReadOnly == true ? ReadOnlyBecause(ReadOnlyReason.Option) : Writable;

    /// <summary>
    /// The effective permissions: read-only when <see cref="DashboardUIOptions.IsReadOnly"/> is set,
    /// otherwise <paramref name="scoped"/> (the cascaded or scope value), or full access when there
    /// is none. Pages and services both go through this, so they can't disagree.
    /// </summary>
    internal static DashboardPermissions Resolve(DashboardPermissions scoped, DashboardUIOptions options)
        => options?.IsReadOnly == true ? ReadOnlyBecause(ReadOnlyReason.Option) : scoped ?? Writable;

    /// <summary>A short explanation of why write actions are refused, for tooltips and error messages.</summary>
    internal string Describe() => Reason switch
    {
        ReadOnlyReason.Option => "The dashboard is configured as read-only.",
        ReadOnlyReason.IsReadOnlyFunc => "Your account has read-only access to this dashboard.",
        ReadOnlyReason.EvaluationFailed => "Your permissions could not be determined, so the dashboard is read-only. See the server log.",
        _ => "Full access.",
    };
}
