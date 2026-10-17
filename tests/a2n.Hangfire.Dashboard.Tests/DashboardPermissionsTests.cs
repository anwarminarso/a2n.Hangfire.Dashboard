using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using a2n.Hangfire.Dashboard.Security;
using a2n.Hangfire.Dashboard.Services;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using HangfireAuthorizationFilter = Hangfire.Dashboard.IDashboardAuthorizationFilter;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Per-page-load permissions: <c>DashboardOptions.IsReadOnlyFunc</c> is evaluated once from the
/// page request (it used to be ignored on the <c>DashboardOptions</c> overload), the result reaches
/// the circuit through the protected <c>DashboardRoutes</c> parameter, and failures fail closed.
/// </summary>
public class DashboardPermissionsTests
{
    private const string RoleHeader = "X-Test-Role";

    // ---------------------------------------------------------------------------------------------
    // DashboardPermissionEvaluator
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void IsReadOnlyOption_IsReadOnly_WithoutCallingTheFunction()
    {
        var calls = 0;
        var options = UiOptionsFrom(new DashboardOptions { IsReadOnlyFunc = _ => { calls++; return false; } });
        options.IsReadOnly = true;

        using var storage = new InMemoryStorage();
        var result = DashboardPermissionEvaluator.Evaluate(NewHttpContext(storage), options, null);

        Assert.Equal(new DashboardPermissions(true, ReadOnlyReason.Option), result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void DefaultHangfireFunction_LeavesTheDashboardWritable()
    {
        using var storage = new InMemoryStorage();
        var options = UiOptionsFrom(new DashboardOptions());

        var result = DashboardPermissionEvaluator.Evaluate(NewHttpContext(storage), options, null);

        Assert.Equal(DashboardPermissions.Writable, result);
    }

    [Fact]
    public void OptionsNotFromHangfire_AreWritable()
    {
        using var storage = new InMemoryStorage();

        var result = DashboardPermissionEvaluator.Evaluate(NewHttpContext(storage), new DashboardUIOptions(), null);

        Assert.Equal(DashboardPermissions.Writable, result);
    }

    [Fact]
    public void RoleBasedFunction_IsEvaluatedPerRequest_Independently()
    {
        using var storage = new InMemoryStorage();
        var options = UiOptionsFrom(new DashboardOptions { IsReadOnlyFunc = IsNotAdmin });

        var results = new ConcurrentDictionary<string, DashboardPermissions>();
        Parallel.ForEach(new[] { "admin", "viewer", "admin", "viewer" }.Select((role, i) => (role, i)), item =>
        {
            var http = NewHttpContext(storage, item.role);
            results[$"{item.role}-{item.i}"] = DashboardPermissionEvaluator.Evaluate(http, options, null);
        });

        Assert.All(results.Where(r => r.Key.StartsWith("admin")), r => Assert.Equal(DashboardPermissions.Writable, r.Value));
        Assert.All(results.Where(r => r.Key.StartsWith("viewer")),
            r => Assert.Equal(new DashboardPermissions(true, ReadOnlyReason.IsReadOnlyFunc), r.Value));
    }

    [Fact]
    public void ThrowingFunction_FailsClosed_AndLogsAWarning()
    {
        using var storage = new InMemoryStorage();
        var options = UiOptionsFrom(new DashboardOptions
        {
            IsReadOnlyFunc = _ => throw new InvalidOperationException("boom"),
        });
        var logger = new CapturingLogger();

        var result = DashboardPermissionEvaluator.Evaluate(NewHttpContext(storage), options, logger);

        Assert.Equal(DashboardPermissions.FailedClosed, result);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.IsType<InvalidOperationException>(entry.Exception);
    }

    [Fact]
    public void MissingRequest_FailsClosed_AndLogsAWarning()
    {
        var options = UiOptionsFrom(new DashboardOptions { IsReadOnlyFunc = _ => false });
        var logger = new CapturingLogger();

        var result = DashboardPermissionEvaluator.Evaluate(null, options, logger);

        Assert.Equal(DashboardPermissions.FailedClosed, result);
        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries).Level);
    }

    [Fact]
    public void Function_ReceivesTheConfiguredDashboardOptionsInstance()
    {
        using var storage = new InMemoryStorage();
        DashboardOptions seen = null;
        var hangfireOptions = new DashboardOptions();
        hangfireOptions.IsReadOnlyFunc = ctx => { seen = ctx.Options; return false; };

        DashboardPermissionEvaluator.Evaluate(NewHttpContext(storage), UiOptionsFrom(hangfireOptions), null);

        Assert.Same(hangfireOptions, seen);
    }

    [Fact]
    public void ApplyTo_CarriesTheHangfireOptions()
    {
        var hangfireOptions = new DashboardOptions();
        var target = new DashboardUIOptions();

        UiOptionsFrom(hangfireOptions).ApplyTo(target);

        Assert.Same(hangfireOptions, target.HangfireOptions);
    }

    // ---------------------------------------------------------------------------------------------
    // DashboardPermissionsState
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void State_IsReadOnlyUntilInitialized_AndTheFirstValueWins()
    {
        var state = new DashboardPermissionsState();
        Assert.False(state.IsInitialized);
        Assert.True(state.IsReadOnly);

        state.Initialize(new DashboardPermissions(true, ReadOnlyReason.IsReadOnlyFunc));
        state.Initialize(DashboardPermissions.Writable);

        Assert.True(state.IsInitialized);
        Assert.Equal(ReadOnlyReason.IsReadOnlyFunc, state.Current.Reason);
        Assert.True(state.IsReadOnly);
    }

    // ---------------------------------------------------------------------------------------------
    // End to end through UseHangfireDashboardUI(path, DashboardOptions)
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("admin", "false", "None")]
    [InlineData("viewer", "true", "IsReadOnlyFunc")]
    public async Task PrerenderedPage_ReflectsThePermissionsOfItsRequest(string role, string readOnly, string reason)
    {
        var calls = 0;
        var hangfireOptions = new DashboardOptions
        {
            Authorization = new HangfireAuthorizationFilter[] { new AllowAllHangfireFilter() },
            IsReadOnlyFunc = ctx => { Interlocked.Increment(ref calls); return IsNotAdmin(ctx); },
        };

        await using var host = await DashboardHost.StartAsync(hangfireOptions, enablePrerendering: true);
        var html = await host.GetPageAsync(role);

        Assert.Contains($"data-hf-readonly=\"{readOnly}\"", html);
        Assert.Contains($"data-hf-readonly-reason=\"{reason}\"", html);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ThePermissionsTravelOnlyInsideTheProtectedDescriptor(bool enablePrerendering)
    {
        var hangfireOptions = new DashboardOptions
        {
            Authorization = new HangfireAuthorizationFilter[] { new AllowAllHangfireFilter() },
            IsReadOnlyFunc = IsNotAdmin,
        };

        await using var host = await DashboardHost.StartAsync(hangfireOptions, enablePrerendering);
        var html = await host.GetPageAsync("viewer");

        var markers = Regex.Matches(html, @"<!--Blazor:(\{.*?\})-->", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .Where(json => json.Contains("\"type\":\"server\"", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(markers);
        Assert.All(markers, json =>
        {
            Assert.Contains("\"descriptor\":", json);
            Assert.DoesNotContain("Permissions", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("IsReadOnly", json, StringComparison.OrdinalIgnoreCase);
        });

        if (!enablePrerendering)
        {
            // Nothing below DashboardRoutes is rendered before the circuit starts.
            Assert.DoesNotContain("data-hf-readonly", html);
        }
    }

    [Fact]
    public async Task ThrowingFunction_RendersReadOnly_AndLogsAWarning()
    {
        var hangfireOptions = new DashboardOptions
        {
            Authorization = new HangfireAuthorizationFilter[] { new AllowAllHangfireFilter() },
            IsReadOnlyFunc = _ => throw new InvalidOperationException("boom"),
        };

        await using var host = await DashboardHost.StartAsync(hangfireOptions, enablePrerendering: true);
        var html = await host.GetPageAsync("admin");

        Assert.Contains("data-hf-readonly=\"true\"", html);
        Assert.Contains("data-hf-readonly-reason=\"EvaluationFailed\"", html);
        Assert.Contains(host.Logs.Entries, e =>
            e.Category == DashboardPermissionEvaluator.LoggerCategory && e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task HangfireAuthorizationFilters_ReceiveTheConfiguredDashboardOptionsInstance()
    {
        var filter = new AllowAllHangfireFilter();
        var hangfireOptions = new DashboardOptions { Authorization = new HangfireAuthorizationFilter[] { filter } };

        await using var host = await DashboardHost.StartAsync(hangfireOptions, enablePrerendering: true);
        await host.GetPageAsync("admin");

        Assert.NotEmpty(filter.SeenOptions);
        Assert.All(filter.SeenOptions, seen => Assert.Same(hangfireOptions, seen));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static bool IsNotAdmin(DashboardContext context)
        => context.GetHttpContext().Request.Headers[RoleHeader] != "admin";

    private static DashboardUIOptions UiOptionsFrom(DashboardOptions hangfireOptions)
        => DashboardUIOptions.FromDashboardOptions(hangfireOptions);

    private static HttpContext NewHttpContext(JobStorage storage, string role = null)
    {
        var services = new ServiceCollection().AddSingleton(storage).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        if (role != null)
            http.Request.Headers[RoleHeader] = role;
        return http;
    }

    private sealed class AllowAllHangfireFilter : HangfireAuthorizationFilter
    {
        public ConcurrentBag<DashboardOptions> SeenOptions { get; } = new();

        public bool Authorize(DashboardContext context)
        {
            SeenOptions.Add(context.Options);
            return true;
        }
    }

    /// <summary>Serves the dashboard through the <c>DashboardOptions</c> overload on a TestServer.</summary>
    private sealed class DashboardHost : IAsyncDisposable
    {
        private readonly InMemoryStorage _storage;
        private readonly WebApplication _app;
        private readonly HttpClient _client;

        private DashboardHost(InMemoryStorage storage, WebApplication app, CapturingLoggerProvider logs)
        {
            _storage = storage;
            _app = app;
            _client = app.GetTestClient();
            Logs = logs;
        }

        public CapturingLoggerProvider Logs { get; }

        public static async Task<DashboardHost> StartAsync(DashboardOptions hangfireOptions, bool enablePrerendering)
        {
            // Registered directly rather than through AddHangfire, which would point Hangfire's
            // global log provider at this host's logger factory (see DashboardDocumentAssetsTests).
            var storage = new InMemoryStorage();
            var logs = new CapturingLoggerProvider();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.Services.AddSingleton<JobStorage>(storage);
            builder.Services.AddHangfireDashboardUI();

            var app = builder.Build();
            var options = DashboardUIOptions.FromDashboardOptions(hangfireOptions);
            options.EnablePrerendering = enablePrerendering;
            app.UseHangfireDashboardUI("/hangfire", options);
            await app.StartAsync();
            return new DashboardHost(storage, app, logs);
        }

        public async Task<string> GetPageAsync(string role)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/hangfire/");
            request.Headers.Add(RoleHeader, role);
            using var response = await _client.SendAsync(request);
            var html = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {html}");
            return html;
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            _storage.Dispose();
        }
    }

    private sealed record LogEntry(string Category, LogLevel Level, string Message, Exception Exception);

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<LogEntry> _sink;

        public CapturingLogger() : this(string.Empty, new ConcurrentQueue<LogEntry>()) { }

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

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> _sink = new();

        public IReadOnlyCollection<LogEntry> Entries => _sink;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _sink);

        public void Dispose() { }
    }
}
