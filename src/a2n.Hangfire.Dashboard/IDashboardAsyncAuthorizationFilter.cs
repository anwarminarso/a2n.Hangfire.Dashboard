using Microsoft.AspNetCore.Http;

namespace a2n.Hangfire.Dashboard;

/// <summary>
/// Async authorization filter for the Hangfire Dashboard UI.
/// </summary>
public interface IDashboardAsyncAuthorizationFilter
{
    /// <summary>
    /// Determines whether the current request is authorized to access the dashboard.
    /// </summary>
    Task<bool> AuthorizeAsync(HttpContext context, CancellationToken cancellationToken = default);
}
