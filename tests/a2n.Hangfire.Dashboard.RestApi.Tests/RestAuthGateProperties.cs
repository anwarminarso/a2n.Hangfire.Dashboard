using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Threading.Tasks;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace a2n.Hangfire.Dashboard.RestApi.Tests;

/// <summary>
/// Property test for the REST API authentication gate.
///
/// Feature: integrations-v2-6, Property 13: REST API authentication gate.
///
/// <para>
/// **Property 13: REST API authentication gate** — for any request to a <c>Rest_Api_Endpoint</c>
/// WITHOUT a valid JWT bearer token, the REST API responds with HTTP 401 and returns no job data.
/// </para>
///
/// **Validates: Requirements 10.1, 10.3, 17.1, 17.2**
///
/// <para>
/// The host (<see cref="RestApiTestHost"/>) registers a fake <c>IStorageQueryProvider</c> and a
/// <c>HangfireMonitorService</c> so the endpoints <em>would</em> return job data if the request were
/// authorized. The generator varies the endpoint path and the missing/invalid-token case; the
/// property asserts every unauthorized request yields HTTP 401 and a body free of any job data.
/// </para>
/// </summary>
public class RestAuthGateProperties
{
    // The host is shared across all cases (and across [Fact]/[Property] methods). FsCheck.Xunit's
    // [Property] runner does not honor IAsyncLifetime, so the host is built lazily and synchronously
    // on first access and reused thereafter.
    private static readonly Lazy<RestApiTestHost> SharedHost = new(() =>
        RestApiTestHost.Create(withMetricsProvider: true).GetAwaiter().GetResult());

    private static RestApiTestHost Host => SharedHost.Value;
    private static readonly Lazy<HttpClient> SharedClient = new(() => Host.CreateClient());
    private HttpClient _client => SharedClient.Value;

    // Distinctive job data the fake providers emit for an AUTHORIZED request. If any of these
    // strings appear in an unauthorized response body, the gate leaked job data.
    private static readonly string[] JobDataMarkers =
    {
        "\"jobId\"", "42", "Acme.Jobs.SendEmail", "\"items\"", "\"totalCount\"", "default",
    };

    /// <summary>The REST API endpoint paths (relative to the /hangfire/api/v1 group).</summary>
    public enum Endpoint
    {
        SearchJobs,
        JobById,
        JobsByState,
        Queues,
        MetricsJobDuration,
        MetricsQueueLatency,
    }

    /// <summary>How the (missing/invalid) bearer token is presented.</summary>
    public enum TokenKind
    {
        None,            // no Authorization header at all
        Garbage,         // "Bearer <non-JWT junk>"
        MalformedBearer, // Authorization header without a usable token
        WrongSignature,  // structurally valid JWT signed with the wrong key
        Expired,         // correctly signed but expired token
    }

    public sealed record Scenario(Endpoint Endpoint, TokenKind Token);

    private static string PathFor(Endpoint endpoint) => endpoint switch
    {
        Endpoint.SearchJobs => $"{RestApiTestHost.PathPrefix}/api/v1/jobs",
        Endpoint.JobById => $"{RestApiTestHost.PathPrefix}/api/v1/jobs/42",
        Endpoint.JobsByState => $"{RestApiTestHost.PathPrefix}/api/v1/jobs/state/Succeeded",
        Endpoint.Queues => $"{RestApiTestHost.PathPrefix}/api/v1/queues",
        Endpoint.MetricsJobDuration => $"{RestApiTestHost.PathPrefix}/api/v1/metrics/job-duration",
        Endpoint.MetricsQueueLatency => $"{RestApiTestHost.PathPrefix}/api/v1/metrics/queue-latency",
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
    };

    private static Gen<Scenario> ScenarioGen =>
        from endpoint in Gen.Elements(
            Endpoint.SearchJobs,
            Endpoint.JobById,
            Endpoint.JobsByState,
            Endpoint.Queues,
            Endpoint.MetricsJobDuration,
            Endpoint.MetricsQueueLatency)
        from token in Gen.Elements(
            TokenKind.None,
            TokenKind.Garbage,
            TokenKind.MalformedBearer,
            TokenKind.WrongSignature,
            TokenKind.Expired)
        select new Scenario(endpoint, token);

    private static Arbitrary<Scenario> ScenarioArb => Arb.From(ScenarioGen);

    private static void ApplyToken(HttpRequestMessage request, TokenKind kind)
    {
        switch (kind)
        {
            case TokenKind.None:
                break;
            case TokenKind.Garbage:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-jwt-token");
                break;
            case TokenKind.MalformedBearer:
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer");
                break;
            case TokenKind.WrongSignature:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateWronglySignedToken());
                break;
            case TokenKind.Expired:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateExpiredToken());
                break;
        }
    }

    private static string CreateWronglySignedToken()
    {
        // Signed with a DIFFERENT key than the host validates against → invalid signature → 401.
        var wrongKey = new SymmetricSecurityKey(
            System.Text.Encoding.UTF8.GetBytes("a-completely-different-256-bit-signing-key-value!!!!"));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = RestApiTestHost.Issuer,
            Audience = RestApiTestHost.Audience,
            Subject = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "attacker") }),
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(wrongKey, SecurityAlgorithms.HmacSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static string CreateExpiredToken()
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = RestApiTestHost.Issuer,
            Audience = RestApiTestHost.Audience,
            Subject = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "test-client") }),
            NotBefore = DateTime.UtcNow.AddMinutes(-10),
            Expires = DateTime.UtcNow.AddMinutes(-5),
            SigningCredentials = new SigningCredentials(RestApiTestHost.SecurityKey, SecurityAlgorithms.HmacSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static bool ContainsJobData(string body)
    {
        foreach (var marker in JobDataMarkers)
        {
            if (body.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    [Property(MaxTest = 100)]
    public Property RestApi_WithoutValidToken_Returns401AndNoJobData()
    {
        return Prop.ForAll(ScenarioArb, scenario =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, PathFor(scenario.Endpoint));
            ApplyToken(request, scenario.Token);

            using var response = _client.SendAsync(request).GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                return false.Label(
                    $"endpoint={scenario.Endpoint} token={scenario.Token}: expected 401, got {(int)response.StatusCode}. Body='{body}'");
            }

            if (ContainsJobData(body))
            {
                return false.Label(
                    $"endpoint={scenario.Endpoint} token={scenario.Token}: 401 body leaked job data. Body='{body}'");
            }

            return true.ToProperty();
        });
    }

    // ── Sanity facts confirming the gate is real (not a blanket 401) ─────────────────────────

    [Fact]
    public async Task SearchJobs_WithValidToken_Returns200WithJobData()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, PathFor(Endpoint.SearchJobs));
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", RestApiTestHost.CreateValidToken());

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Acme.Jobs.SendEmail", body);
    }

    [Fact]
    public async Task SearchJobs_WithoutToken_Returns401AndNoJobData()
    {
        using var response = await _client.GetAsync(PathFor(Endpoint.SearchJobs));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("Acme.Jobs.SendEmail", body);
    }
}
