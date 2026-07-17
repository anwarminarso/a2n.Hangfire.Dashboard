using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace a2n.Hangfire.Dashboard.RestApi.Tests;

/// <summary>
/// Property test for invalid REST API query parameters.
///
/// Feature: integrations-v2-6, Property 12: REST invalid parameter yields 400.
///
/// <para>
/// **Property 12: REST invalid parameter yields 400** — for any request whose query parameters are
/// invalid (non-numeric or out-of-range <c>page</c>/<c>pageSize</c>, or an unknown <c>state</c>), the
/// REST API responds with HTTP 400 and a descriptive problem-details body, and SHALL NOT return job
/// data.
/// </para>
///
/// **Validates: Requirements 9.5**
///
/// <para>
/// Every request carries a VALID bearer token (<see cref="RestApiTestHost.CreateValidToken"/>) so it
/// passes the authentication gate (task 10.5) and reaches parameter validation — otherwise an
/// unauthorized request would short-circuit with HTTP 401 before validation runs. The generator emits
/// invalid <c>page</c>/<c>pageSize</c>/<c>state</c> values against the <c>/jobs</c> and
/// <c>/jobs/state/{state}</c> endpoints; the property asserts every case yields HTTP 400 and a body
/// free of any job data.
/// </para>
/// </summary>
public class RestInvalidParameterProperties
{
    // FsCheck.Xunit's [Property] runner does not honor IAsyncLifetime, so the host is built lazily
    // and synchronously on first access and reused thereafter (mirrors task 10.5's pattern).
    private static readonly Lazy<RestApiTestHost> SharedHost = new(() =>
        RestApiTestHost.Create(withMetricsProvider: true).GetAwaiter().GetResult());

    private static RestApiTestHost Host => SharedHost.Value;
    private static readonly Lazy<HttpClient> SharedClient = new(() => Host.CreateClient());
    private HttpClient _client => SharedClient.Value;

    private const string Base = RestApiTestHost.PathPrefix + "/api/v1";

    // Distinctive job data the fake providers emit for an AUTHORIZED, VALID request. None of these
    // strings may appear in a 400 body — a validation failure must not return job data (Req 9.5).
    private static readonly string[] JobDataMarkers =
    {
        "Acme.Jobs.SendEmail", "\"items\"", "\"totalCount\"", "\"jobId\"",
    };

    /// <summary>Which query parameter is being made invalid, and on which endpoint.</summary>
    public enum InvalidKind
    {
        JobsPage,          // GET /jobs?page=<invalid>
        JobsPageSize,      // GET /jobs?pageSize=<invalid>
        JobsState,         // GET /jobs?state=<unknown>
        JobsByStatePath,   // GET /jobs/state/<unknown>
    }

    public sealed record Scenario(InvalidKind Kind, string Value);

    // Non-numeric, non-positive, and above-int-range page values (default MaxPageSize = 500).
    private static readonly string[] InvalidPageValues =
    {
        "abc", "3x", "1.5", "-1", "0", "-999", " ", "99999999999999999999",
    };

    // Non-numeric, zero, negative, and above-MaxPageSize (>500) page-size values.
    private static readonly string[] InvalidPageSizeValues =
    {
        "abc", "xyz", "0", "-5", "1.5", "501", "1000", "99999999999999999999",
    };

    // States outside the canonical Hangfire set (case-insensitive) → unknown state.
    private static readonly string[] UnknownStateValues =
    {
        "Bogus", "unknown", "Runningg", "Enqueuedd", "xyz", "NotAState",
    };

    private static Gen<Scenario> ScenarioGen =>
        Gen.OneOf(
            from v in Gen.Elements(InvalidPageValues) select new Scenario(InvalidKind.JobsPage, v),
            from v in Gen.Elements(InvalidPageSizeValues) select new Scenario(InvalidKind.JobsPageSize, v),
            from v in Gen.Elements(UnknownStateValues) select new Scenario(InvalidKind.JobsState, v),
            from v in Gen.Elements(UnknownStateValues) select new Scenario(InvalidKind.JobsByStatePath, v));

    private static Arbitrary<Scenario> ScenarioArb => Arb.From(ScenarioGen);

    private static string PathFor(Scenario s)
    {
        var encoded = Uri.EscapeDataString(s.Value);
        return s.Kind switch
        {
            InvalidKind.JobsPage => $"{Base}/jobs?page={encoded}",
            InvalidKind.JobsPageSize => $"{Base}/jobs?pageSize={encoded}",
            InvalidKind.JobsState => $"{Base}/jobs?state={encoded}",
            InvalidKind.JobsByStatePath => $"{Base}/jobs/state/{encoded}",
            _ => throw new ArgumentOutOfRangeException(nameof(s)),
        };
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

    private HttpResponseMessage SendWithValidToken(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", RestApiTestHost.CreateValidToken());
        return _client.SendAsync(request).GetAwaiter().GetResult();
    }

    [Property(MaxTest = 100)]
    public Property RestApi_WithInvalidParameter_Returns400AndNoJobData()
    {
        return Prop.ForAll(ScenarioArb, scenario =>
        {
            var path = PathFor(scenario);
            using var response = SendWithValidToken(path);
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (response.StatusCode != HttpStatusCode.BadRequest)
            {
                return false.Label(
                    $"kind={scenario.Kind} value='{scenario.Value}': expected 400, got {(int)response.StatusCode}. Body='{body}'");
            }

            if (ContainsJobData(body))
            {
                return false.Label(
                    $"kind={scenario.Kind} value='{scenario.Value}': 400 body leaked job data. Body='{body}'");
            }

            // A descriptive body: problem-details responses carry a non-empty payload.
            if (string.IsNullOrWhiteSpace(body))
            {
                return false.Label(
                    $"kind={scenario.Kind} value='{scenario.Value}': 400 body was empty (expected a descriptive message).");
            }

            return true.ToProperty();
        });
    }

    // ── Sanity facts confirming the gate discriminates valid from invalid ────────────────────

    [Fact]
    public async Task SearchJobs_WithValidTokenAndValidPaging_Returns200WithJobData()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/jobs?page=1&pageSize=20");
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", RestApiTestHost.CreateValidToken());

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Acme.Jobs.SendEmail", body);
    }

    [Fact]
    public async Task SearchJobs_WithValidTokenAndInvalidPageSize_Returns400NoJobData()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/jobs?pageSize=99999");
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", RestApiTestHost.CreateValidToken());

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("Acme.Jobs.SendEmail", body);
    }

    [Fact]
    public async Task JobsByState_WithValidTokenAndUnknownState_Returns400NoJobData()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/jobs/state/Bogus");
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", RestApiTestHost.CreateValidToken());

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("Acme.Jobs.SendEmail", body);
    }
}
