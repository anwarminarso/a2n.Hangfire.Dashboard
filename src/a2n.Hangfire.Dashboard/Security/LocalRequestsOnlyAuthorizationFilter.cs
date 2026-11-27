using System.Net;
using Microsoft.AspNetCore.Http;

namespace a2n.Hangfire.Dashboard.Security;

/// <summary>
/// Allows access only from local requests (loopback), matching Hangfire's default dashboard policy.
/// </summary>
public sealed class LocalRequestsOnlyAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(HttpContext context)
    {
        var remoteIp = context.Connection.RemoteIpAddress;
        if (remoteIp == null)
            return false;

        if (IPAddress.IsLoopback(remoteIp))
            return true;

        var localIp = context.Connection.LocalIpAddress;
        if (localIp != null && remoteIp.Equals(localIp))
            return true;

        return false;
    }
}
