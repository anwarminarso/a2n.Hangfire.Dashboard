using Microsoft.AspNetCore.Http;

namespace a2n.Hangfire.Dashboard.Security;

/// <summary>
/// Lets every request through. Use it only when access is controlled somewhere else, for example
/// authentication enforced by a reverse proxy in front of the dashboard, or a network that only
/// trusted operators can reach.
/// </summary>
/// <remarks>
/// <para>
/// Empty <see cref="DashboardUIOptions.Authorization"/> and
/// <see cref="DashboardUIOptions.AsyncAuthorization"/> lists no longer mean "allow everyone": the
/// dashboard then falls back to local requests only. Opening it up has to be declared with this
/// filter, and the dashboard logs a warning at startup when it does.
/// </para>
/// <para>
/// The health endpoint in <see cref="HealthCheckAuthorization.RequireDashboardAuth"/> mode uses
/// the same filters, so it is open too.
/// </para>
/// </remarks>
public sealed class AllowAllRequestsAuthorizationFilter : IDashboardAuthorizationFilter, IDashboardAsyncAuthorizationFilter
{
    /// <inheritdoc />
    public bool Authorize(HttpContext context) => true;

    /// <inheritdoc />
    public Task<bool> AuthorizeAsync(HttpContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(true);
}
