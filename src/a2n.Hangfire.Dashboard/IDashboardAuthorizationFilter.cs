using Microsoft.AspNetCore.Http;

namespace a2n.Hangfire.Dashboard;

/// <summary>
/// Authorization filter for the Hangfire Dashboard UI.
/// </summary>
public interface IDashboardAuthorizationFilter
{
    /// <summary>
    /// Determines whether the current request is authorized to access the dashboard.
    /// </summary>
    /// <param name="context">The HTTP context</param>
    /// <returns>True if authorized, false otherwise</returns>
    bool Authorize(HttpContext context);
}
