using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.RestApi;
using a2n.Hangfire.Dashboard.Services;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace a2n.Hangfire.Dashboard.RestApi.Tests;

/// <summary>
/// Reusable in-memory test host for the read-only REST API package
/// (<c>a2n.Hangfire.Dashboard.RestApi</c>). Builds a real ASP.NET Core pipeline with JWT bearer
/// authentication configured (using a known symmetric signing key), registers the providers the
/// endpoints read through (a fake <see cref="IStorageQueryProvider"/> and a
/// <see cref="HangfireMonitorService"/> over a mocked <see cref="Hangfire.JobStorage"/>), calls
/// <see cref="RestApiDashboardExtensions.AddHangfireDashboardRestApi"/>, and maps the endpoint group
/// with <see cref="RestApiDashboardExtensions.MapHangfireDashboardRestApi"/>.
///
/// <para>
/// Because the endpoints could return real job data <em>if authorized</em>, this fixture makes the
/// authentication gate meaningful: requests without a valid bearer token are rejected by the JWT
/// middleware with HTTP 401 before any provider is touched, while a validly-signed token passes.
/// </para>
///
/// <para>
/// This fixture is intentionally shared by the REST API test tasks: the authentication-gate property
/// test (task 10.5), the invalid-parameter property test (task 10.3), and the REST integration tests
/// (task 10.7) all build their host through <see cref="Create"/>.
/// </para>
/// </summary>
public sealed class RestApiTestHost : IAsyncDisposable
{
    // Known JWT parameters shared by the host's validation and the test's token issuance.
    public const string Issuer = "https://test.hangfire.local/issuer";
    public const string Audience = "hangfire-restapi-tests";
    public const string SigningKey = "restapi-tests-signing-key-must-be-at-least-256-bits-long!!";
    public const string PathPrefix = "/hangfire";

    private readonly IHost _host;

    private RestApiTestHost(IHost host) => _host = host;

    /// <summary>The in-memory <see cref="TestServer"/> hosting the REST API pipeline.</summary>
    public TestServer Server => _host.GetTestServer();

    /// <summary>Creates an <see cref="HttpClient"/> that talks to the in-memory host.</summary>
    public HttpClient CreateClient() => Server.CreateClient();

    /// <summary>The symmetric key used to sign and validate tokens for the host.</summary>
    public static SymmetricSecurityKey SecurityKey { get; } =
        new(Encoding.UTF8.GetBytes(SigningKey));

    /// <summary>
    /// Builds and starts an in-memory REST API host.
    /// </summary>
    /// <param name="pathPrefix">Dashboard path prefix the API group is mounted under.</param>
    /// <param name="withMetricsProvider">
    /// When <see langword="true"/>, registers a fake <see cref="IStorageMetricsProvider"/> so the
    /// metrics-backed endpoints are available; when <see langword="false"/> they degrade to 404.
    /// </param>
    /// <param name="queryProvider">
    /// Optional custom query provider. When null a stub returning a single-record page is used so
    /// authorized calls can return job data.
    /// </param>
    public static async Task<RestApiTestHost> Create(
        string pathPrefix = PathPrefix,
        bool withMetricsProvider = false,
        IStorageQueryProvider queryProvider = null)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddRouting();

                    // Provider the endpoints read through — a stub that returns job data so an
                    // authorized request would produce a non-empty response.
                    services.AddSingleton(queryProvider ?? CreateDefaultQueryProvider());

                    // HangfireMonitorService over a mocked JobStorage/IMonitoringApi so /jobs/{id}
                    // and /queues could return data if the request were authorized.
                    services.AddSingleton(CreateMonitorService());

                    if (withMetricsProvider)
                        services.AddSingleton(CreateMetricsProvider());

                    // JWT bearer authentication configured by the "host" with a known signing key.
                    services
                        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                        .AddJwtBearer(options =>
                        {
                            options.TokenValidationParameters = new TokenValidationParameters
                            {
                                ValidateIssuer = true,
                                ValidIssuer = Issuer,
                                ValidateAudience = true,
                                ValidAudience = Audience,
                                ValidateIssuerSigningKey = true,
                                IssuerSigningKey = SecurityKey,
                                ValidateLifetime = true,
                                ClockSkew = TimeSpan.FromSeconds(5),
                            };
                        });

                    services.AddAuthorization();

                    // Register the REST API services (endpoint services + OpenAPI generation).
                    services.AddHangfireDashboardRestApi();
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapHangfireDashboardRestApi(pathPrefix);
                    });
                });
            })
            .StartAsync();

        return new RestApiTestHost(host);
    }

    /// <summary>
    /// Issues a validly-signed JWT bearer token accepted by the host's configured scheme. Used by
    /// the "with a valid token → 200" sanity assertions.
    /// </summary>
    public static string CreateValidToken()
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "test-client"),
                new Claim(ClaimTypes.Name, "test-client"),
            }),
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(SecurityKey, SecurityAlgorithms.HmacSha256),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static IStorageQueryProvider CreateDefaultQueryProvider()
    {
        var record = new JobSummaryDto
        {
            JobId = "42",
            JobName = "Acme.Jobs.SendEmail",
            State = "Succeeded",
            Queue = "default",
            CreatedAt = DateTime.UtcNow,
        };

        var page = new PagedResult<JobSummaryDto>
        {
            Items = new[] { record },
            TotalCount = 1,
            Page = 1,
            PageSize = 20,
        };

        var mock = new Mock<IStorageQueryProvider>();
        mock.Setup(p => p.GetJobsWithFilterAsync(
                It.IsAny<JobFilterCriteria>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);
        mock.Setup(p => p.GetJobsByStateAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);
        return mock.Object;
    }

    private static HangfireMonitorService CreateMonitorService()
    {
        var details = new JobDetailsDto
        {
            CreatedAt = DateTime.UtcNow,
            Job = null,
            History = new List<StateHistoryDto>
            {
                new() { StateName = "Succeeded", Reason = "done", CreatedAt = DateTime.UtcNow },
            },
            Properties = new Dictionary<string, string>(),
        };

        var api = new Mock<IMonitoringApi>();
        api.Setup(m => m.JobDetails(It.IsAny<string>())).Returns(details);
        api.Setup(m => m.Queues()).Returns(new List<QueueWithTopEnqueuedJobsDto>
        {
            new() { Name = "default", Length = 1 },
        });

        var connection = new Mock<JobStorageConnection>();
        var storage = new Mock<global::Hangfire.JobStorage>();
        storage.Setup(s => s.GetMonitoringApi()).Returns(api.Object);
        storage.Setup(s => s.GetConnection()).Returns(connection.Object);
        storage.Setup(s => s.GetReadOnlyConnection()).Returns(connection.Object);

        return new HangfireMonitorService(storage.Object);
    }

    private static IStorageMetricsProvider CreateMetricsProvider()
    {
        var mock = new Mock<IStorageMetricsProvider>();
        return mock.Object;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}
