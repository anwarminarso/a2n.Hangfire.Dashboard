using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Hangfire;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using a2n.Hangfire.Dashboard.Services;
using SucceededPage = a2n.Hangfire.Dashboard.Components.Pages.Jobs.Succeeded;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Issue #64's acceptance criterion, on real pages: with a storage that stops answering (every
/// call blocks, as when the connection pool is exhausted), many open tabs must not block more
/// threads than <see cref="PollingOptions.MaxConcurrentStorageCalls"/>, and a page's refresh must
/// not pile up behind a stalled one.
/// </summary>
public sealed class PageRefreshStarvationTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly ManualResetEventSlim _release = new();
    private readonly BunitContext _ctx = new();
    private int _running;
    private int _maxRunning;
    private int _calls;

    public PageRefreshStarvationTests()
    {
        var monitoring = new Mock<IMonitoringApi>();
        monitoring.Setup(m => m.SucceededListCount()).Returns(() =>
        {
            Interlocked.Increment(ref _calls);
            var now = Interlocked.Increment(ref _running);
            int max;
            while (now > (max = Volatile.Read(ref _maxRunning))
                   && Interlocked.CompareExchange(ref _maxRunning, now, max) != max) { }
            try
            {
                _release.Wait(Wait);
                return 0;
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        });
        monitoring.Setup(m => m.SucceededJobs(It.IsAny<int>(), It.IsAny<int>()))
            .Returns(new JobList<SucceededJobDto>(Enumerable.Empty<KeyValuePair<string, SucceededJobDto>>()));
        var storage = new Mock<JobStorage>();
        storage.Setup(s => s.GetMonitoringApi()).Returns(monitoring.Object);

        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddSingleton(new DashboardUIOptions
        {
            Polling = new PollingOptions
            {
                MaxConcurrentStorageCalls = 2,
                PageRefreshInterval = TimeSpan.FromMilliseconds(20),
            },
        });
        _ctx.Services.AddDashboardPolling();
        _ctx.Services.AddSingleton(new HangfireMonitorService(storage.Object));
    }

    public void Dispose()
    {
        _release.Set();
        _ctx.Dispose();
        _release.Dispose();
    }

    [Fact]
    public async Task TenOpenPages_OnAStalledStorage_BlockNoMoreThreadsThanTheLimit()
    {
        var gate = _ctx.Services.GetRequiredService<DashboardStorageGate>();
        var pages = Enumerable.Range(0, 10).Select(_ => _ctx.Render<SucceededPage>()).ToList();

        await WaitUntil(() => Volatile.Read(ref _running) == 2);
        // Ten pages, each with a load in flight and a 20 ms refresh loop: under the old timers that
        // would be ten blocked loads plus one more blocked thread per tick per page.
        await Task.Delay(500);
        Assert.Equal(2, Volatile.Read(ref _maxRunning));
        Assert.Equal(2, gate.InFlight);
        Assert.Equal(2, Volatile.Read(ref _calls));

        _release.Set();
        foreach (var page in pages)
            page.WaitForState(() => !page.Markup.Contains("Loading..."), Wait);
        Assert.Equal(2, Volatile.Read(ref _maxRunning));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not reached.");
            await Task.Delay(10);
        }
    }
}
