using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using a2n.Hangfire.Dashboard.Security;
using a2n.Hangfire.Dashboard.Services;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// No authorization filter at all no longer means "allow everyone": the dashboard falls back to
/// local requests and says so at startup. Opening it up takes an explicit
/// <see cref="AllowAllRequestsAuthorizationFilter"/>, which is also logged.
/// </summary>
public class DashboardAuthorizationFallbackTests
{
    private static readonly IPAddress Remote = IPAddress.Parse("10.0.0.5");
    private static readonly IPAddress Server = IPAddress.Parse("10.0.0.1");

    public static TheoryData<string> NoFilterShapes => new() { "null/null", "empty/empty", "null/empty", "empty/null" };

    private static DashboardUIOptions WithoutFilters(string shape)
    {
        var parts = shape.Split('/');
        return new DashboardUIOptions
        {
            Authorization = parts[0] == "null" ? null : Array.Empty<IDashboardAuthorizationFilter>(),
            AsyncAuthorization = parts[1] == "null" ? null : Array.Empty<IDashboardAsyncAuthorizationFilter>(),
        };
    }

    [Theory]
    [MemberData(nameof(NoFilterShapes))]
    public async Task NoFilters_RejectRemoteRequests(string shape)
    {
        Assert.False(await DashboardAuthorization.IsAuthorizedAsync(Request(Remote), WithoutFilters(shape)));
    }

    [Theory]
    [MemberData(nameof(NoFilterShapes))]
    public async Task NoFilters_AcceptLocalRequests(string shape)
    {
        Assert.True(await DashboardAuthorization.IsAuthorizedAsync(Request(IPAddress.Loopback), WithoutFilters(shape)));
    }

    [Fact]
    public async Task NoFilters_RejectARequestWithoutARemoteAddress()
    {
        // Reverse proxies over a Unix socket and TestServer leave the remote address empty.
        Assert.False(await DashboardAuthorization.IsAuthorizedAsync(Request(null), WithoutFilters("null/null")));
    }

    [Fact]
    public async Task NoSyncFilter_WithAPassingAsyncFilter_IsNotTheFallback()
    {
        var options = new DashboardUIOptions
        {
            Authorization = null,
            AsyncAuthorization = new IDashboardAsyncAuthorizationFilter[] { new FixedAsyncFilter(true) },
        };

        Assert.True(await DashboardAuthorization.IsAuthorizedAsync(Request(Remote), options));
    }

    [Fact]
    public async Task AllowAll_AsASyncFilter_AcceptsRemoteRequests()
    {
        var options = new DashboardUIOptions
        {
            Authorization = new IDashboardAuthorizationFilter[] { new AllowAllRequestsAuthorizationFilter() },
            AsyncAuthorization = Array.Empty<IDashboardAsyncAuthorizationFilter>(),
        };

        Assert.True(await DashboardAuthorization.IsAuthorizedAsync(Request(Remote), options));
        Assert.True(DashboardAuthorization.AllowsEveryone(options));
    }

    [Fact]
    public async Task AllowAll_OnlyAsAnAsyncFilter_AcceptsRemoteRequests()
    {
        var options = new DashboardUIOptions
        {
            Authorization = Array.Empty<IDashboardAuthorizationFilter>(),
            AsyncAuthorization = new IDashboardAsyncAuthorizationFilter[] { new AllowAllRequestsAuthorizationFilter() },
        };

        Assert.True(await DashboardAuthorization.IsAuthorizedAsync(Request(Remote), options));
        Assert.True(DashboardAuthorization.AllowsEveryone(options));
    }

    [Fact]
    public void AllowAll_NextToARealFilter_DoesNotOpenTheDashboard()
    {
        var options = new DashboardUIOptions
        {
            Authorization = new IDashboardAuthorizationFilter[]
            {
                new AllowAllRequestsAuthorizationFilter(), new LocalRequestsOnlyAuthorizationFilter(),
            },
        };

        Assert.False(DashboardAuthorization.AllowsEveryone(options));
    }

    [Fact]
    public void Startup_NoFilters_WarnsAndNamesTheExplicitFilter()
    {
        var logger = new CapturingLogger();

        DashboardAuthorization.LogStartupWarnings(WithoutFilters("empty/empty"), logger);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(nameof(AllowAllRequestsAuthorizationFilter), entry.Message);
        Assert.Contains("local requests", entry.Message);
    }

    [Fact]
    public void Startup_AllowAll_WarnsThatTheDashboardIsOpen()
    {
        var logger = new CapturingLogger();
        var options = new DashboardUIOptions
        {
            Authorization = new IDashboardAuthorizationFilter[] { new AllowAllRequestsAuthorizationFilter() },
        };

        DashboardAuthorization.LogStartupWarnings(options, logger);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("without authentication", entry.Message);
    }

    [Fact]
    public void Startup_TheDefaultFilter_LogsNothing()
    {
        var logger = new CapturingLogger();

        DashboardAuthorization.LogStartupWarnings(new DashboardUIOptions(), logger);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void AllowAll_DoesNotBypassReadOnly()
    {
        using var storage = new InMemoryStorage();
        var options = new DashboardUIOptions
        {
            IsReadOnly = true,
            Authorization = new IDashboardAuthorizationFilter[] { new AllowAllRequestsAuthorizationFilter() },
        };
        var monitor = new HangfireMonitorService(storage, null, options, new JobMethodResolver());

        Assert.Throws<InvalidOperationException>(() => monitor.TriggerRecurringJob("any"));
    }

    private static HttpContext Request(IPAddress remote)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remote;
        context.Connection.LocalIpAddress = Server;
        return context;
    }

    private sealed class FixedAsyncFilter(bool result) : IDashboardAsyncAuthorizationFilter
    {
        public Task<bool> AuthorizeAsync(HttpContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyCollection<LogEntry> Entries => _entries;

        public IDisposable BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter)
            => _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception)));
    }
}
