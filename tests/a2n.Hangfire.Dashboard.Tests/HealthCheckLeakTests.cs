using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using a2n.Hangfire.Dashboard.Middleware;
using a2n.Hangfire.Dashboard.Services;
using Hangfire;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using AspNetHealthStatus = Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus;
using HealthCheckContext = Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext;
using HealthCheckRegistration = Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckRegistration;
using IHealthCheck = Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Storage exception messages (which can carry database host names, addresses and connection
/// details) never reach a health response, on any path: the dashboard's probes, the
/// <c>/healthz*</c> endpoint (including its own failure path), and the ASP.NET Core adapter. They
/// stay in the server log. Callers who aren't dashboard users get only statuses and check names.
/// </summary>
public class HealthCheckLeakTests
{
    /// <summary>A unique string standing in for a connection detail in an exception message.</summary>
    private const string Marker = "leak-probe-db.local:5432";

    private static InvalidOperationException Leak() => new($"Could not connect to {Marker} (password=hunter2)");

    // ---------------------------------------------------------------------------------------------
    // HealthCheckService: every probe that can throw
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task StorageProbe_ReportsTheExceptionType_AndLogsTheMessage()
    {
        var logger = new CapturingLogger<HealthCheckService>();
        var service = new HealthCheckService(Monitor(storageFails: true), new DashboardUIOptions(), logger);

        var report = await service.CheckFullAsync();

        Assert.DoesNotContain(Marker, Json(report));
        Assert.Contains(nameof(InvalidOperationException), report.Checks["storage"].Description);
        Assert.Contains(logger.Entries, e => e.Exception?.Message.Contains(Marker) == true);
    }

    [Fact]
    public async Task EveryOtherProbe_ReportsTheExceptionType_AndLogsTheMessage()
    {
        var logger = new CapturingLogger<HealthCheckService>();
        var service = new HealthCheckService(Monitor(storageFails: false), new DashboardUIOptions(), logger);

        var report = await service.CheckFullAsync();

        Assert.DoesNotContain(Marker, Json(report));
        foreach (var check in new[] { "servers", "queue_depth", "stuck_processing", "failure_rate", "recurring_jobs" })
        {
            Assert.Contains(nameof(InvalidOperationException), report.Checks[check].Description);
            Assert.Contains("server log", report.Checks[check].Description);
        }
        // One warning per failed probe, each with the full exception.
        Assert.Equal(5, logger.Entries.Count(e => e.Level == LogLevel.Warning && e.Exception?.Message.Contains(Marker) == true));
    }

    // ---------------------------------------------------------------------------------------------
    // The /healthz endpoints
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/healthz", HealthCheckAuthorization.AllowAnonymous)]
    [InlineData("/healthz/ready", HealthCheckAuthorization.AllowAnonymous)]
    [InlineData("/healthz/full", HealthCheckAuthorization.AllowAnonymous)]
    [InlineData("/healthz/full", HealthCheckAuthorization.LocalOnly)]
    public async Task AnonymousCallers_GetStatusesAndCheckNamesOnly(string path, HealthCheckAuthorization mode)
    {
        var options = new DashboardUIOptions
        {
            HealthCheckAuthorizationMode = mode,
            Authorization = new IDashboardAuthorizationFilter[] { new FixedFilter(false) },
        };
        // Storage fails, so every endpoint reports Unhealthy with the marker in the raw description.
        using var provider = Provider(options, Monitor(storageFails: true));

        var (status, body) = await GetAsync(provider, options, path);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
        Assert.DoesNotContain(Marker, body);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal(new[] { "checks", "status" }, root.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        Assert.All(root.GetProperty("checks").EnumerateObject(), check =>
            Assert.Equal(new[] { "status" }, check.Value.EnumerateObject().Select(p => p.Name)));
    }

    [Fact]
    public async Task DashboardUsers_GetTheFullReport_WithoutTheMessage()
    {
        var options = new DashboardUIOptions
        {
            Authorization = new IDashboardAuthorizationFilter[] { new FixedFilter(true) },
        };
        using var provider = Provider(options, Monitor(storageFails: false));

        var (_, body) = await GetAsync(provider, options, "/healthz/full");

        Assert.DoesNotContain(Marker, body);
        Assert.Contains("\"description\"", body);
        Assert.Contains("\"version\"", body);
        Assert.Contains(nameof(InvalidOperationException), body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndpointFailurePath_NeverSendsTheMessage(bool dashboardUser)
    {
        var options = new DashboardUIOptions
        {
            // Open, so an anonymous caller reaches the failure path too.
            HealthCheckAuthorizationMode = HealthCheckAuthorization.AllowAnonymous,
            Authorization = new IDashboardAuthorizationFilter[] { new FixedFilter(dashboardUser) },
        };
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton<ILoggerFactory>(new LoggerFactory(new[] { logs }));
        // The report can't be computed at all: the scope can't build the health check service.
        services.AddScoped<HealthCheckService>(_ => throw Leak());
        services.AddSingleton<HealthReportCache>();
        using var provider = services.BuildServiceProvider();

        var (status, body) = await GetAsync(provider, options, "/healthz/full");

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
        Assert.DoesNotContain(Marker, body);
        Assert.Equal(dashboardUser, body.Contains("\"error\""));
        Assert.Contains(logs.Entries, e => e.Exception?.Message.Contains(Marker) == true);
    }

    [Fact]
    public async Task AThrowingAuthorizationFilter_CountsAsAnonymous()
    {
        var options = new DashboardUIOptions
        {
            HealthCheckAuthorizationMode = HealthCheckAuthorization.AllowAnonymous,
            Authorization = new IDashboardAuthorizationFilter[] { new ThrowingFilter() },
        };
        using var provider = Provider(options, Monitor(storageFails: false));

        var (status, body) = await GetAsync(provider, options, "/healthz/full");

        Assert.NotEqual(StatusCodes.Status401Unauthorized, status);
        Assert.DoesNotContain("\"description\"", body);
    }

    // ---------------------------------------------------------------------------------------------
    // Who may call which endpoint (HealthCheckAuthorizationMode)
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/healthz", false, StatusCodes.Status200OK)]
    [InlineData("/healthz/live", false, StatusCodes.Status200OK)]
    [InlineData("/healthz/ready", false, StatusCodes.Status200OK)]
    [InlineData("/healthz/full", false, StatusCodes.Status401Unauthorized)]
    [InlineData("/healthz/full", true, StatusCodes.Status200OK)]
    public async Task Default_ProbesAreOpen_TheFullReportNeedsTheDashboardsAuthorization(
        string path, bool dashboardUser, int expected)
    {
        var options = new DashboardUIOptions
        {
            Authorization = new IDashboardAuthorizationFilter[] { new FixedFilter(dashboardUser) },
        };
        Assert.Equal(HealthCheckAuthorization.AnonymousProbes, options.HealthCheckAuthorizationMode);
        using var provider = Provider(options, HealthyMonitor());

        var (status, _) = await GetAsync(provider, options, path);

        Assert.Equal(expected, status);
    }

    [Fact]
    public async Task AllowAnonymous_OpensTheFullReport_Redacted()
    {
        var options = new DashboardUIOptions
        {
            HealthCheckAuthorizationMode = HealthCheckAuthorization.AllowAnonymous,
            Authorization = new IDashboardAuthorizationFilter[] { new FixedFilter(false) },
        };
        using var provider = Provider(options, HealthyMonitor());

        var (status, body) = await GetAsync(provider, options, "/healthz/full");

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.DoesNotContain("\"description\"", body);
    }

    [Theory]
    [InlineData(null, StatusCodes.Status401Unauthorized)]
    [InlineData("10.0.0.5", StatusCodes.Status401Unauthorized)]
    [InlineData("127.0.0.1", StatusCodes.Status200OK)]
    [InlineData("10.0.0.1", StatusCodes.Status200OK)] // the server's own address
    public async Task LocalOnly_TreatsAMissingRemoteAddressAsRemote(string remote, int expected)
    {
        var options = new DashboardUIOptions
        {
            HealthCheckAuthorizationMode = HealthCheckAuthorization.LocalOnly,
            Authorization = new IDashboardAuthorizationFilter[] { new FixedFilter(false) },
        };
        using var provider = Provider(options, HealthyMonitor());

        var (status, _) = await GetAsync(provider, options, "/healthz",
            remote is null ? null : IPAddress.Parse(remote), remoteIsMissing: remote is null);

        Assert.Equal(expected, status);
    }

    // ---------------------------------------------------------------------------------------------
    // The ASP.NET Core adapter (AddHangfireDashboard)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Adapter_DescriptionAndData_CarryNoMessage()
    {
        var options = new DashboardUIOptions();
        using var provider = Provider(options, Monitor(storageFails: false));
        var check = new HangfireDashboardHealthCheck(provider.GetRequiredService<HealthReportCache>(), options);

        var result = await check.CheckHealthAsync(ContextFor(check));

        Assert.DoesNotContain(Marker, result.Description);
        Assert.DoesNotContain(result.Data.Values, v => v?.ToString()?.Contains(Marker) == true);
        Assert.Contains(result.Data.Keys, k => k.EndsWith(".description"));
        Assert.Null(result.Exception);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adapter_WhenTheProbeThrows_PassesTheExceptionOnlyWhenOptedIn(bool includeException)
    {
        var options = new DashboardUIOptions { IncludeExceptionInHealthCheckResult = includeException };
        var logs = new CapturingLoggerProvider();
        var check = new HangfireDashboardHealthCheck(ThrowingCache(options), options, new LoggerFactory(new[] { logs }));

        var result = await check.CheckHealthAsync(ContextFor(check));

        Assert.DoesNotContain(Marker, result.Description);
        Assert.Equal(includeException, result.Exception is not null);
        Assert.Contains(logs.Entries, e => e.Exception?.Message.Contains(Marker) == true);
    }

    [Fact]
    public async Task HostHealthEndpoint_WithAUIResponseWriterLikeWriter_ShowsNoMessage()
    {
        // HealthChecks.UI's UIResponseWriter writes each entry's description, data and
        // Exception?.Message; this writer does the same.
        var options = new DashboardUIOptions();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(options);
        services.AddScoped<HealthCheckService>(_ => throw Leak());
        services.AddSingleton<HealthReportCache>();
        services.AddHealthChecks().AddHangfireDashboard();
        using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService>()
            .CheckHealthAsync();
        var written = JsonSerializer.Serialize(report.Entries.ToDictionary(e => e.Key, e => new
        {
            status = e.Value.Status.ToString(),
            description = e.Value.Description,
            data = e.Value.Data.ToDictionary(d => d.Key, d => d.Value?.ToString()),
            exception = e.Value.Exception?.Message,
        }));

        Assert.Equal(AspNetHealthStatus.Unhealthy, report.Status);
        Assert.DoesNotContain(Marker, written);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A monitor whose storage throws <see cref="Leak"/>: on the statistics read (so the storage
    /// probe fails), or on every read the other probes make while the statistics succeed.
    /// </summary>
    private static HangfireMonitorService Monitor(bool storageFails)
    {
        var api = new Mock<IMonitoringApi>();
        if (storageFails)
            api.Setup(m => m.GetStatistics()).Throws(Leak());
        else
            api.Setup(m => m.GetStatistics()).Returns(new StatisticsDto());
        api.Setup(m => m.Servers()).Throws(Leak());
        api.Setup(m => m.Queues()).Throws(Leak());
        api.Setup(m => m.ProcessingJobs(It.IsAny<int>(), It.IsAny<int>())).Throws(Leak());
        api.Setup(m => m.HourlySucceededJobs()).Throws(Leak());
        api.Setup(m => m.HourlyFailedJobs()).Throws(Leak());

        var storage = new Mock<JobStorage>();
        storage.Setup(s => s.GetMonitoringApi()).Returns(api.Object);
        storage.Setup(s => s.GetReadOnlyConnection()).Throws(Leak());
        storage.Setup(s => s.GetConnection()).Throws(Leak());
        return new HangfireMonitorService(storage.Object);
    }

    private static ServiceProvider Provider(DashboardUIOptions options, HangfireMonitorService monitor)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(options);
        services.AddSingleton(monitor);
        services.AddScoped(sp => new HealthCheckService(monitor, options,
            sp.GetService<ILoggerFactory>()?.CreateLogger<HealthCheckService>()));
        services.AddSingleton<HealthReportCache>();
        return services.BuildServiceProvider();
    }

    private static HealthReportCache ThrowingCache(DashboardUIOptions options)
    {
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddScoped<HealthCheckService>(_ => throw Leak());
        return new HealthReportCache(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), options);
    }

    /// <summary>A monitor whose storage answers every probe with an empty, healthy picture.</summary>
    private static HangfireMonitorService HealthyMonitor()
    {
        var api = new Mock<IMonitoringApi>();
        api.Setup(m => m.GetStatistics()).Returns(new StatisticsDto());
        api.Setup(m => m.Servers()).Returns(new List<ServerDto> { new() { Name = "srv", Heartbeat = DateTime.UtcNow } });
        api.Setup(m => m.Queues()).Returns(new List<QueueWithTopEnqueuedJobsDto>());
        api.Setup(m => m.ProcessingJobs(It.IsAny<int>(), It.IsAny<int>()))
            .Returns(new JobList<ProcessingJobDto>(new List<KeyValuePair<string, ProcessingJobDto>>()));
        api.Setup(m => m.HourlySucceededJobs()).Returns(new Dictionary<DateTime, long>());
        api.Setup(m => m.HourlyFailedJobs()).Returns(new Dictionary<DateTime, long>());

        var storage = new Mock<JobStorage>();
        storage.Setup(s => s.GetMonitoringApi()).Returns(api.Object);
        return new HangfireMonitorService(storage.Object);
    }

    private static readonly IPAddress ServerAddress = IPAddress.Parse("10.0.0.1");

    /// <param name="remote">The caller's address. Set explicitly (loopback by default) rather than
    /// left null, since a missing address counts as remote.</param>
    /// <param name="remoteIsMissing">Leave the remote address null on purpose.</param>
    private static async Task<(int Status, string Body)> GetAsync(IServiceProvider provider, DashboardUIOptions options,
        string path, IPAddress remote = null, bool remoteIsMissing = false)
    {
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Path = path;
        context.Connection.LocalIpAddress = ServerAddress;
        context.Connection.RemoteIpAddress = remoteIsMissing ? null : remote ?? IPAddress.Loopback;
        context.Response.Body = new MemoryStream();

        Assert.True(await HealthCheckEndpoint.TryHandleAsync(context, options));

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private static HealthCheckContext ContextFor(IHealthCheck check) => new()
    {
        Registration = new HealthCheckRegistration("hangfire_dashboard", check, AspNetHealthStatus.Unhealthy, null),
    };

    private static string Json(HealthReport report) => JsonSerializer.Serialize(report);

    private sealed class FixedFilter(bool result) : IDashboardAuthorizationFilter
    {
        public bool Authorize(HttpContext context) => result;
    }

    private sealed class ThrowingFilter : IDashboardAuthorizationFilter
    {
        public bool Authorize(HttpContext context) => throw new InvalidOperationException("filter failed");
    }

    private sealed record LogEntry(string Category, LogLevel Level, string Message, Exception Exception);

    private class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<LogEntry> _sink;

        public CapturingLogger(string category, ConcurrentQueue<LogEntry> sink)
        {
            _category = category;
            _sink = sink;
        }

        public IReadOnlyCollection<LogEntry> Entries => _sink;

        public IDisposable BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter)
            => _sink.Enqueue(new LogEntry(_category, logLevel, formatter(state, exception), exception));
    }

    private sealed class CapturingLogger<T> : CapturingLogger, ILogger<T>
    {
        public CapturingLogger() : base(typeof(T).FullName, new ConcurrentQueue<LogEntry>()) { }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> _sink = new();

        public IReadOnlyCollection<LogEntry> Entries => _sink;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _sink);

        public void Dispose() { }
    }
}
