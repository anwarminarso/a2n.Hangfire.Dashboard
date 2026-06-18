using System;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Middleware;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests.Integration;

/// <summary>
/// Integration test for heatmap authorization behavior (task 21.2).
///
/// The heatmap page has no authorization code of its own — it is gated by the shared
/// <see cref="DashboardMiddleware"/> + <see cref="a2n.Hangfire.Dashboard.Security.DashboardAuthorization"/>
/// exactly like every other dashboard page, wired by <c>UseHangfireDashboardUI</c> via
/// <c>app.Map(pathMatch, branch =&gt; branch.UseMiddleware&lt;DashboardMiddleware&gt;(options))</c>.
///
/// These tests stand up a minimal in-memory ASP.NET Core pipeline that mirrors that exact wiring:
/// the real production <see cref="DashboardMiddleware"/> runs in a mapped branch in front of a
/// terminal handler that stands in for the Blazor heatmap render and emits a recognizable content
/// marker. Because the marker is only written when the middleware authorizes the request (calls
/// <c>_next</c>), the tests can assert that a denied/unauthenticated request returns NO heatmap
/// content while producing the same access-control outcome as other dashboard pages.
///
/// Validates:
///   * Req 14.1 — authorization filters run before any heatmap content is returned.
///   * Req 14.2 — an authenticated-but-denied request gets 403 and no heatmap content.
///   * Req 14.3 — an unauthenticated request with LoginPath configured is redirected (302) to the
///     login path with a returnUrl, and no heatmap content.
///   * Req 14.4 — an unauthenticated request with no LoginPath gets 401 and no heatmap content.
/// </summary>
public class HeatmapAuthorizationIntegrationTests
{
    private const string DashboardPathMatch = "/hangfire";
    private const string HeatmapRoute = "/heatmap";
    private const string HeatmapRequestPath = DashboardPathMatch + HeatmapRoute;

    // Stand-in for the rendered heatmap page. Must never appear in a denied/unauthenticated response.
    private const string HeatmapContentMarker = "HEATMAP_PAGE_CONTENT_MARKER";

    /// <summary>Authorization filter that denies every request (simulates an auth policy refusal).</summary>
    private sealed class DenyAllFilter : IDashboardAuthorizationFilter
    {
        public bool Authorize(HttpContext context) => false;
    }

    /// <summary>Authorization filter that allows every request (positive control).</summary>
    private sealed class AllowAllFilter : IDashboardAuthorizationFilter
    {
        public bool Authorize(HttpContext context) => true;
    }

    /// <summary>
    /// Builds an in-memory test server whose pipeline mirrors <c>UseHangfireDashboardUI</c>: the
    /// real <see cref="DashboardMiddleware"/> guards a mapped dashboard branch in front of a
    /// terminal handler that emits <see cref="HeatmapContentMarker"/> (the heatmap render stand-in).
    /// </summary>
    /// <param name="options">The dashboard options (authorization filters + optional login path).</param>
    /// <param name="authenticateRequest">
    /// When true, an upstream middleware marks the request as authenticated so the test can
    /// distinguish the authenticated-denied (403) path from the unauthenticated (401/redirect) path.
    /// </param>
    private static async Task<IHost> BuildHostAsync(DashboardUIOptions options, bool authenticateRequest)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.Configure(app =>
                {
                    if (authenticateRequest)
                    {
                        // Simulate an authenticated principal (as a real auth middleware would) so
                        // the shared authorization code takes the "authenticated but denied" branch (403).
                        app.Use(async (ctx, next) =>
                        {
                            var identity = new ClaimsIdentity(authenticationType: "TestAuth");
                            identity.AddClaim(new Claim(ClaimTypes.Name, "test-user"));
                            ctx.User = new ClaimsPrincipal(identity);
                            await next();
                        });
                    }

                    // Mirror the production branch wiring from UseHangfireDashboardUI.
                    app.Map(DashboardPathMatch, branch =>
                    {
                        branch.UseMiddleware<DashboardMiddleware>(options);

                        // Terminal handler — only reached when the middleware authorizes the request.
                        branch.Run(async ctx =>
                        {
                            ctx.Response.StatusCode = StatusCodes.Status200OK;
                            await ctx.Response.WriteAsync(HeatmapContentMarker);
                        });
                    });
                });
            })
            .StartAsync();

        return host;
    }

    private static async Task<(HttpStatusCode Status, string Body, Uri Location)> GetHeatmapAsync(
        DashboardUIOptions options, bool authenticateRequest)
    {
        using var host = await BuildHostAsync(options, authenticateRequest);
        // TestServer client does not auto-follow redirects, so a 302 is observed directly.
        using var client = host.GetTestServer().CreateClient();

        using var response = await client.GetAsync(HeatmapRequestPath);
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, body, response.Headers.Location);
    }

    [Fact]
    public async Task AuthenticatedButDenied_Returns403_AndNoHeatmapContent()
    {
        // Req 14.1 + 14.2: filters run first; a denied authenticated request gets 403 with no content.
        var options = new DashboardUIOptions
        {
            Authorization = new IDashboardAuthorizationFilter[] { new DenyAllFilter() },
            AsyncAuthorization = Array.Empty<IDashboardAsyncAuthorizationFilter>(),
            LoginPath = null,
        };

        var (status, body, location) = await GetHeatmapAsync(options, authenticateRequest: true);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Null(location);
        Assert.DoesNotContain(HeatmapContentMarker, body);
    }

    [Fact]
    public async Task Unauthenticated_NoLoginPath_Returns401_AndNoHeatmapContent()
    {
        // Req 14.1 + 14.4: an unauthenticated request with no LoginPath gets 401 with no content.
        var options = new DashboardUIOptions
        {
            Authorization = new IDashboardAuthorizationFilter[] { new DenyAllFilter() },
            AsyncAuthorization = Array.Empty<IDashboardAsyncAuthorizationFilter>(),
            LoginPath = null,
        };

        var (status, body, location) = await GetHeatmapAsync(options, authenticateRequest: false);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Null(location);
        Assert.DoesNotContain(HeatmapContentMarker, body);
    }

    [Fact]
    public async Task Unauthenticated_WithLoginPath_RedirectsToLoginWithReturnUrl_AndNoHeatmapContent()
    {
        // Req 14.1 + 14.3: an unauthenticated request with LoginPath is redirected (302) with a
        // returnUrl pointing back at the heatmap, and no heatmap content is returned.
        const string loginPath = "/account/login";
        var options = new DashboardUIOptions
        {
            Authorization = new IDashboardAuthorizationFilter[] { new DenyAllFilter() },
            AsyncAuthorization = Array.Empty<IDashboardAsyncAuthorizationFilter>(),
            LoginPath = loginPath,
        };

        var (status, body, location) = await GetHeatmapAsync(options, authenticateRequest: false);

        Assert.Equal(HttpStatusCode.Redirect, status); // 302
        Assert.NotNull(location);

        var target = location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
        Assert.StartsWith(loginPath, target);
        // returnUrl carries the dashboard-prefixed heatmap path (PathBase + Path), URL-encoded.
        Assert.Contains("returnUrl=", target);
        Assert.Contains(Uri.EscapeDataString(HeatmapRequestPath), target);

        Assert.DoesNotContain(HeatmapContentMarker, body);
    }

    [Fact]
    public async Task Authorized_Returns200_WithHeatmapContent()
    {
        // Positive control: when authorization passes, the heatmap render IS reached. This proves
        // the negative assertions above are meaningful (the marker can appear when allowed).
        var options = new DashboardUIOptions
        {
            Authorization = new IDashboardAuthorizationFilter[] { new AllowAllFilter() },
            AsyncAuthorization = Array.Empty<IDashboardAsyncAuthorizationFilter>(),
            LoginPath = null,
        };

        var (status, body, location) = await GetHeatmapAsync(options, authenticateRequest: true);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(location);
        Assert.Contains(HeatmapContentMarker, body);
    }
}
