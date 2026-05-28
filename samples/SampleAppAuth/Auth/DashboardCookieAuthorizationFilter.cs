using a2n.Hangfire.Dashboard;
using Microsoft.AspNetCore.Http;

namespace SampleAppAuth.Auth;

/// <summary>
/// Allows dashboard access only for authenticated users (cookie auth).
/// </summary>
public sealed class DashboardCookieAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(HttpContext context)
        => context.User?.Identity?.IsAuthenticated == true;
}
