using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace a2n.Hangfire.Dashboard.RestApi.Tests;

/// <summary>
/// End-to-end integration tests for the read-only REST API package
/// (<c>a2n.Hangfire.Dashboard.RestApi</c>), exercised through a real in-memory ASP.NET Core pipeline
/// built by <see cref="RestApiTestHost"/> (JWT bearer auth + the providers the endpoints read
/// through). These are xUnit <c>[Fact]</c> tests that build a host per scenario (with/without a
/// metrics provider, under a custom path prefix) and assert the observable HTTP behavior.
///
/// <para>
/// Coverage:
/// <list type="bullet">
///   <item>Endpoints reachable under a NON-DEFAULT dashboard path prefix (Req 16.1, 5.5).</item>
///   <item>Expected JSON shapes reusing the existing providers — <c>GET /jobs</c> returns a
///   <c>PagedResponse&lt;JobRecordDto&gt;</c>, <c>GET /queues</c> the queue list, <c>GET /jobs/{id}</c>
///   the job details (Req 9.1, 9.2).</item>
///   <item>Metrics-backed endpoint degrades to HTTP 404 while query endpoints stay up when no
///   <c>IStorageMetricsProvider</c> is registered (Req 9.6).</item>
///   <item>A valid JWT issued for the host's scheme authorizes the endpoints (Req 10.2).</item>
///   <item>The generated OpenAPI document describes every endpoint and declares the bearer scheme
///   (Req 11.1).</item>
/// </list>
/// </para>
///
/// **Validates: Requirements 5.5, 9.1, 9.2, 9.6, 10.2, 11.1, 16.1**
/// </summary>
public class RestApiIntegrationTests
{
    // A non-default dashboard path prefix to prove the API group and OpenAPI document remain
    // reachable when the dashboard is mounted somewhere other than "/hangfire" (Req 16.1).
    private const string CustomPrefix = "/ops/hangfire";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static HttpRequestMessage AuthorizedGet(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", RestApiTestHost.CreateValidToken());
        return request;
    }

    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpClient client, string path)
    {
        using var request = AuthorizedGet(path);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, body);
    }

    // ── Reachability under a non-default path prefix + valid-JWT authorization ────────────────

    [Fact]
    public async Task Jobs_UnderNonDefaultPrefix_WithValidToken_Returns200()
    {
        await using var host = await RestApiTestHost.Create(pathPrefix: CustomPrefix);
        using var client = host.CreateClient();

        var (status, body) = await SendAsync(client, $"{CustomPrefix}/api/v1/jobs");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Acme.Jobs.SendEmail", body);
    }

    [Fact]
    public async Task AllQueryEndpoints_UnderNonDefaultPrefix_WithValidToken_Return200()
    {
        await using var host = await RestApiTestHost.Create(pathPrefix: CustomPrefix);
        using var client = host.CreateClient();

        var jobs = await SendAsync(client, $"{CustomPrefix}/api/v1/jobs");
        var jobById = await SendAsync(client, $"{CustomPrefix}/api/v1/jobs/42");
        var jobsByState = await SendAsync(client, $"{CustomPrefix}/api/v1/jobs/state/Succeeded");
        var queues = await SendAsync(client, $"{CustomPrefix}/api/v1/queues");

        Assert.Equal(HttpStatusCode.OK, jobs.Status);
        Assert.Equal(HttpStatusCode.OK, jobById.Status);
        Assert.Equal(HttpStatusCode.OK, jobsByState.Status);
        Assert.Equal(HttpStatusCode.OK, queues.Status);
    }

    // ── Expected JSON shapes (reusing the existing providers) ─────────────────────────────────

    [Fact]
    public async Task Jobs_ReturnsPagedResponseShape()
    {
        await using var host = await RestApiTestHost.Create(pathPrefix: CustomPrefix);
        using var client = host.CreateClient();

        var (status, body) = await SendAsync(client, $"{CustomPrefix}/api/v1/jobs");

        Assert.Equal(HttpStatusCode.OK, status);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // PagedResponse<T> envelope: items + paging metadata (Req 9.3).
        Assert.Equal(JsonValueKind.Array, root.GetProperty("items").ValueKind);
        Assert.True(root.TryGetProperty("totalCount", out var totalCount));
        Assert.Equal(1, totalCount.GetInt64());
        Assert.Equal(1, root.GetProperty("page").GetInt32());
        Assert.True(root.TryGetProperty("pageSize", out _));
        Assert.True(root.TryGetProperty("totalPages", out _));
        Assert.True(root.TryGetProperty("hasNextPage", out _));
        Assert.True(root.TryGetProperty("hasPreviousPage", out _));

        // JobRecordDto item shape.
        var item = root.GetProperty("items")[0];
        Assert.Equal("42", item.GetProperty("jobId").GetString());
        Assert.Equal("Acme.Jobs.SendEmail", item.GetProperty("jobName").GetString());
        Assert.Equal("Succeeded", item.GetProperty("state").GetString());
        Assert.Equal("default", item.GetProperty("queue").GetString());
    }

    [Fact]
    public async Task Queues_ReturnsQueueList()
    {
        await using var host = await RestApiTestHost.Create(pathPrefix: CustomPrefix);
        using var client = host.CreateClient();

        var (status, body) = await SendAsync(client, $"{CustomPrefix}/api/v1/queues");

        Assert.Equal(HttpStatusCode.OK, status);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.NotEmpty(root.EnumerateArray());

        var first = root[0];
        Assert.Equal("default", first.GetProperty("name").GetString());
        Assert.Equal(1, first.GetProperty("length").GetInt64());
    }

    [Fact]
    public async Task JobById_ReturnsJobDetailsShape()
    {
        await using var host = await RestApiTestHost.Create(pathPrefix: CustomPrefix);
        using var client = host.CreateClient();

        var (status, body) = await SendAsync(client, $"{CustomPrefix}/api/v1/jobs/42");

        Assert.Equal(HttpStatusCode.OK, status);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal("42", root.GetProperty("jobId").GetString());
        Assert.Equal("Succeeded", root.GetProperty("state").GetString());
        Assert.True(root.TryGetProperty("createdAt", out _));
        Assert.Equal(JsonValueKind.Array, root.GetProperty("history").ValueKind);
        Assert.NotEmpty(root.GetProperty("history").EnumerateArray());
        Assert.Equal("Succeeded", root.GetProperty("history")[0].GetProperty("stateName").GetString());
    }

    // ── Metrics endpoint 404 while query endpoints stay up (no metrics provider) ──────────────

    [Fact]
    public async Task MetricsEndpoint_Returns404_WhileQueryEndpointsStayUp_WhenNoMetricsProvider()
    {
        await using var host = await RestApiTestHost.Create(
            pathPrefix: CustomPrefix, withMetricsProvider: false);
        using var client = host.CreateClient();

        var metrics = await SendAsync(client, $"{CustomPrefix}/api/v1/metrics/job-duration");
        var jobs = await SendAsync(client, $"{CustomPrefix}/api/v1/jobs");

        // Metrics-backed endpoint degrades to 404 (Req 9.6) ...
        Assert.Equal(HttpStatusCode.NotFound, metrics.Status);
        // ... but the query-backed endpoints remain available.
        Assert.Equal(HttpStatusCode.OK, jobs.Status);
    }

    // ── OpenAPI document describes every endpoint + declares the bearer scheme ────────────────

    [Fact]
    public async Task OpenApiDocument_IsAnonymous_DescribesEndpoints_AndDeclaresBearerScheme()
    {
        await using var host = await RestApiTestHost.Create(pathPrefix: CustomPrefix);
        using var client = host.CreateClient();

        // The OpenAPI document route allows anonymous access so generators can fetch it (Req 11.2).
        // The documented route is {prefix}/api/v1/openapi/v1.json (RestApiOptions.OpenApiRoutePath).
        using var response = await client.GetAsync($"{CustomPrefix}/api/v1/openapi/v1.json");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var paths = root.GetProperty("paths");
        var pathKeys = paths.EnumerateObject().Select(p => p.Name).ToList();

        // Every endpoint is described. Path keys carry the full route (including the prefix), so we
        // match on the meaningful suffix to stay robust across net8 (Swashbuckle) and net9+/net10.
        Assert.Contains(pathKeys, k => k.EndsWith("/jobs", StringComparison.Ordinal));
        Assert.Contains(pathKeys, k => k.EndsWith("/jobs/{id}", StringComparison.Ordinal));
        Assert.Contains(pathKeys, k => k.EndsWith("/jobs/state/{state}", StringComparison.Ordinal));
        Assert.Contains(pathKeys, k => k.EndsWith("/queues", StringComparison.Ordinal));

        // A JWT bearer security scheme is declared (Req 11.3).
        var schemes = root.GetProperty("components").GetProperty("securitySchemes");
        Assert.True(schemes.TryGetProperty("Bearer", out var bearer),
            $"Expected a 'Bearer' security scheme in the OpenAPI document. Body='{body}'");
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
    }
}
