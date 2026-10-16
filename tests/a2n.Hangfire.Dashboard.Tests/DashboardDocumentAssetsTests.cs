using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AngleSharp.Html.Parser;
using a2n.Hangfire.Dashboard.Components;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Issue #87: every page load gets its document from <see cref="DashboardApp"/>, with or without
/// prerendering. It loads the dashboard's own scripts and stylesheet with a <c>?v=</c> cache-buster,
/// so a browser fetches them again after an upgrade instead of running a cached copy of the previous
/// version. The vendored, minified libraries are loaded without one.
/// </summary>
public class DashboardDocumentAssetsTests
{
    private const string ResourcePrefix = "a2n.Hangfire.Dashboard.Content.";

    private static readonly string Version = typeof(DashboardApp).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(DashboardApp).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    private sealed class AllowAllFilter : IDashboardAuthorizationFilter
    {
        public bool Authorize(HttpContext context) => true;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EveryScriptAndStylesheetOfTheDashboard_CarriesTheVersion(bool enablePrerendering)
    {
        var assets = await GetDocumentAssetsAsync(enablePrerendering);

        var own = OwnAssets();
        Assert.NotEmpty(own);
        foreach (var path in own)
        {
            Assert.Contains($"_content/{path}?v={Version}", assets);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnlyVendoredLibrariesAreLoadedWithoutTheVersion(bool enablePrerendering)
    {
        var assets = await GetDocumentAssetsAsync(enablePrerendering);

        var unversioned = assets.Where(a => a.StartsWith("_content/", StringComparison.Ordinal) && !a.Contains("?v="));
        Assert.All(unversioned, a => Assert.True(
            a.StartsWith("_content/lib/", StringComparison.Ordinal) || a.Contains(".min."),
            $"{a} is the dashboard's own file but is loaded without ?v="));
    }

    [Fact]
    public async Task TheDocumentLoadsTheSameAssets_WithAndWithoutPrerendering()
    {
        Assert.Equal(await GetDocumentAssetsAsync(true), await GetDocumentAssetsAsync(false));
    }

    /// <summary>The dashboard's own, unminified scripts and stylesheets, as paths under <c>_content/</c>.</summary>
    private static List<string> OwnAssets()
        => typeof(DashboardApp).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix + "js.", StringComparison.Ordinal)
                        || n.StartsWith(ResourcePrefix + "css.", StringComparison.Ordinal))
            .Where(n => (n.EndsWith(".js", StringComparison.Ordinal) || n.EndsWith(".css", StringComparison.Ordinal))
                        && !n.Contains(".min.", StringComparison.Ordinal))
            .Select(n =>
            {
                var rest = n.Substring(ResourcePrefix.Length); // "js.nav.js"
                var dot = rest.IndexOf('.');
                return rest.Substring(0, dot) + "/" + rest.Substring(dot + 1);
            })
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Serves the dashboard's page through <c>UseHangfireDashboardUI</c> and returns the
    /// <c>src</c> of its scripts and the <c>href</c> of its stylesheets, in document order.
    /// </summary>
    private static async Task<List<string>> GetDocumentAssetsAsync(bool enablePrerendering)
    {
        // The storage is registered directly rather than through AddHangfire, which points
        // Hangfire's global log provider at this host's logger factory: once the host is disposed,
        // every other test that creates a storage would fail.
        using var storage = new InMemoryStorage();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<JobStorage>(storage);
        builder.Services.AddHangfireDashboardUI();

        await using var app = builder.Build();
        app.UseHangfireDashboardUI("/hangfire", new DashboardUIOptions
        {
            Authorization = new IDashboardAuthorizationFilter[] { new AllowAllFilter() },
            AsyncAuthorization = Array.Empty<IDashboardAsyncAuthorizationFilter>(),
            EnablePrerendering = enablePrerendering,
        });
        await app.StartAsync();

        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/hangfire/");
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {html}");

        var document = new HtmlParser().ParseDocument(html);
        var assets = document.QuerySelectorAll("script[src]").Select(e => e.GetAttribute("src"))
            .Concat(document.QuerySelectorAll("link[rel=stylesheet]").Select(e => e.GetAttribute("href")))
            .ToList();

        await app.StopAsync();
        return assets;
    }
}
