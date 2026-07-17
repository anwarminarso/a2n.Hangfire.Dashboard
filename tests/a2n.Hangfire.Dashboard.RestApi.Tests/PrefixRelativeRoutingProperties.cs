using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace a2n.Hangfire.Dashboard.RestApi.Tests;

/// <summary>
/// Property test for prefix-relative routing of the read-only REST API.
///
/// Feature: integrations-v2-6, Property 19: Prefix-relative routing.
///
/// <para>
/// **Property 19: Prefix-relative routing** — for any <c>Path_Prefix</c>, the REST routes and
/// self-links begin with that prefix (mounted at <c>{prefix}/api/v1</c>) and contain no hard-coded
/// <c>/hangfire</c> when the configured prefix is not itself <c>hangfire</c>.
/// </para>
///
/// **Validates: Requirements 16.1, 16.2**
///
/// <para>
/// Approach: <b>hosted</b>. <see cref="RestApiDashboardExtensions.BuildGroupPrefix"/> is
/// <c>internal</c> and this test project has no <c>InternalsVisibleTo</c> access, so the pure check
/// is not available. Instead the property builds an in-memory host per configured prefix (via
/// <see cref="RestApiTestHost.Create"/>) and asserts, with a valid bearer token, that
/// <c>GET {prefix}/api/v1/jobs</c> → 200 while <c>GET /hangfire/api/v1/jobs</c> → 404 whenever the
/// configured prefix is not <c>hangfire</c> — proving the group is mounted relative to the configured
/// prefix rather than a hard-coded <c>/hangfire</c>.
/// </para>
///
/// <para>
/// The generator draws from a curated set of representative prefixes (custom short prefix, nested
/// paths, missing leading slash, different case, trailing slash, and a prefix that legitimately
/// contains the word "hangfire"). Hosts and clients are cached per prefix so the property stays
/// reliable and fast even at <c>MaxTest = 100</c> (only a handful of distinct hosts are ever built).
/// </para>
/// </summary>
public class PrefixRelativeRoutingProperties
{
    // Representative Path_Prefix strings exercising the normalization/mounting behavior.
    private static readonly string[] RepresentativePrefixes =
    {
        "/hf",                 // short custom prefix
        "/ops/hangfire",       // nested prefix that legitimately contains "hangfire"
        "/a/b/c",              // deep nested prefix
        "hangfire",            // no leading slash → normalizes to the default
        "/HangFire",           // different case → routes case-insensitively to /hangfire
        "/hangfire/",          // trailing slash → normalizes to the default
        "/metrics-dash/",      // custom prefix with trailing slash
        "/tools/jobs",         // nested custom prefix
    };

    // One host (and client) per distinct configured prefix, built lazily and reused across
    // iterations. FsCheck.Xunit's [Property] runner does not honor IAsyncLifetime, so hosts are
    // created synchronously on first use.
    private static readonly ConcurrentDictionary<string, (RestApiTestHost Host, HttpClient Client)> Hosts = new();

    private static HttpClient ClientFor(string pathPrefix)
    {
        var entry = Hosts.GetOrAdd(pathPrefix, p =>
        {
            var host = RestApiTestHost.Create(pathPrefix: p).GetAwaiter().GetResult();
            return (host, host.CreateClient());
        });
        return entry.Client;
    }

    /// <summary>
    /// Replicates <c>RestApiDashboardExtensions.BuildGroupPrefix</c> (which is <c>internal</c>) using
    /// the package's <b>public</b> <see cref="RestApiDashboardExtensions.ApiVersionSegment"/> constant:
    /// trim surrounding whitespace and slashes, then compose <c>/{prefix}/api/v1</c>.
    /// </summary>
    private static string ExpectedGroupPrefix(string pathPrefix)
    {
        var prefix = (pathPrefix ?? string.Empty).Trim().Trim('/');
        return prefix.Length == 0
            ? $"/{RestApiDashboardExtensions.ApiVersionSegment}"
            : $"/{prefix}/{RestApiDashboardExtensions.ApiVersionSegment}";
    }

    private static string NormalizedPrefix(string pathPrefix) =>
        (pathPrefix ?? string.Empty).Trim().Trim('/');

    private static Arbitrary<string> PrefixArb =>
        Arb.From(Gen.Elements(RepresentativePrefixes));

    private static HttpResponseMessage GetWithValidToken(HttpClient client, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", RestApiTestHost.CreateValidToken());
        return client.SendAsync(request).GetAwaiter().GetResult();
    }

    [Property(MaxTest = 100)]
    public Property RestRoutes_AreMountedRelativeToPrefix_WithNoHardCodedHangfire()
    {
        return Prop.ForAll(PrefixArb, pathPrefix =>
        {
            var normalized = NormalizedPrefix(pathPrefix);
            var groupPrefix = ExpectedGroupPrefix(pathPrefix);
            var client = ClientFor(pathPrefix);

            // (1) The composed route begins with the configured prefix and ends with the API segment.
            var expectedStart = normalized.Length == 0 ? "/" : $"/{normalized}";
            if (!groupPrefix.StartsWith(expectedStart, StringComparison.Ordinal))
            {
                return false.Label(
                    $"prefix='{pathPrefix}': group '{groupPrefix}' does not begin with '{expectedStart}'");
            }

            if (!groupPrefix.EndsWith($"/{RestApiDashboardExtensions.ApiVersionSegment}", StringComparison.Ordinal))
            {
                return false.Label(
                    $"prefix='{pathPrefix}': group '{groupPrefix}' does not end with the API version segment");
            }

            // (2) No hard-coded '/hangfire' unless the configured prefix legitimately contains it.
            if (!normalized.Contains("hangfire", StringComparison.OrdinalIgnoreCase)
                && groupPrefix.Contains("hangfire", StringComparison.OrdinalIgnoreCase))
            {
                return false.Label(
                    $"prefix='{pathPrefix}': group '{groupPrefix}' contains hard-coded 'hangfire'");
            }

            // (3) Hosted: the group is actually reachable at the configured prefix (200 with a token).
            using var atPrefix = GetWithValidToken(client, $"{groupPrefix}/jobs");
            if (atPrefix.StatusCode != HttpStatusCode.OK)
            {
                var body = atPrefix.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return false.Label(
                    $"prefix='{pathPrefix}': GET {groupPrefix}/jobs expected 200, got {(int)atPrefix.StatusCode}. Body='{body}'");
            }

            // (4) Hosted: when the prefix is not 'hangfire', the hard-coded /hangfire path is NOT
            //     mounted (404) — proving relative mounting, not a fixed base path. (Routing is
            //     case-insensitive, so '/HangFire' normalizes to 'hangfire' and is skipped here.)
            if (!string.Equals(normalized, "hangfire", StringComparison.OrdinalIgnoreCase))
            {
                using var atHangfire = GetWithValidToken(client, "/hangfire/api/v1/jobs");
                if (atHangfire.StatusCode != HttpStatusCode.NotFound)
                {
                    return false.Label(
                        $"prefix='{pathPrefix}': GET /hangfire/api/v1/jobs expected 404, got {(int)atHangfire.StatusCode}");
                }
            }

            return true.ToProperty();
        });
    }

    // ── Sanity fact: a representative non-default prefix mounts relative to the prefix ───────────

    [Fact]
    public async Task NonDefaultPrefix_MountsJobsRelativeToPrefix_AndNotAtHangfire()
    {
        var client = ClientFor("/hf");

        using var atPrefix = new HttpRequestMessage(HttpMethod.Get, "/hf/api/v1/jobs");
        atPrefix.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", RestApiTestHost.CreateValidToken());
        using var atPrefixResponse = await client.SendAsync(atPrefix);
        Assert.Equal(HttpStatusCode.OK, atPrefixResponse.StatusCode);

        using var atHangfire = new HttpRequestMessage(HttpMethod.Get, "/hangfire/api/v1/jobs");
        atHangfire.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", RestApiTestHost.CreateValidToken());
        using var atHangfireResponse = await client.SendAsync(atHangfire);
        Assert.Equal(HttpStatusCode.NotFound, atHangfireResponse.StatusCode);
    }
}
