using System;
using System.Reflection;
using System.Threading;
using Bunit;
using Hangfire;
using Hangfire.InMemory;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Components.Pages;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// The Home page's history chart reads go through the storage gate. Called synchronously from
/// OnAfterRenderAsync, a slow storage blocked a thread-pool thread and froze the circuit per open
/// Home tab, outside MaxConcurrentStorageCalls (found by the release stress test, tier S1).
/// </summary>
public class HomeHistoryGateTests
{
    [Fact]
    public void AStalledHistoryRead_RunsOnAGateSlot_AndLeavesTheCircuitResponsive()
    {
        var storage = new StallingStorage(new InMemoryStorage());
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        Register(ctx, storage);
        var gate = ctx.Services.GetRequiredService<DashboardStorageGate>();

        storage.Stall();
        try
        {
            var cut = ctx.Render<Home>();
            storage.WaitUntilStalled();

            // The history read holds a gate slot (the health card's failure-rate check stalls on the
            // same read, but runs outside the gate in this setup)...
            cut.WaitForAssertion(() => Assert.Equal(1, gate.InFlight), TestTimeouts.RenderWait);

            // ...and the circuit still takes realtime updates while it is stuck.
            var captured = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
            ctx.Services.GetRequiredService<DashboardMetricsFeed>().PublishMetrics(new DashboardMetricsSnapshot
            {
                Statistics = new StatisticsDto(),
                CapturedAtUtc = captured,
            });
            cut.WaitForAssertion(
                () => Assert.Equal(captured.Ticks.ToString(),
                    cut.Find("[data-testid=metrics-snapshot]").GetAttribute("data-snapshot-at")),
                TestTimeouts.RenderWait);
        }
        finally
        {
            storage.Release();
        }

        SpinWait.SpinUntil(() => gate.InFlight == 0, TestTimeouts.RenderWait);
        Assert.Equal(0, gate.InFlight);
    }

    private static void Register(BunitContext ctx, JobStorage storage)
    {
        var options = new DashboardUIOptions();
        ctx.Services.AddSingleton(options);
        ctx.Services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(new Microsoft.AspNetCore.Http.HttpContextAccessor());
        ctx.Services.AddScoped<AuditActorAccessor>();
        ctx.Services.AddScoped(sp => new AuditLogService(storage, options,
            sp.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>(), sp.GetService<AuditActorAccessor>()));
        ctx.Services.AddScoped(sp => new HangfireMonitorService(storage, sp.GetRequiredService<AuditLogService>()));
        ctx.Services.AddScoped(sp => new HealthCheckService(sp.GetRequiredService<HangfireMonitorService>(), options, null));
        ctx.Services.AddSingleton<HealthReportCache>();
        ctx.Services.AddDashboardPolling();
    }

    /// <summary>Hangfire.InMemory whose hourly history read can be held until released.</summary>
    internal sealed class StallingStorage(JobStorage inner) : JobStorage
    {
        private readonly ManualResetEventSlim _open = new(initialState: true);
        private readonly ManualResetEventSlim _stalled = new(initialState: false);

        public void Stall() => _open.Reset();

        public void Release() => _open.Set();

        public void WaitUntilStalled()
        {
            if (!_stalled.Wait(TestTimeouts.RenderWait))
                throw new TimeoutException("The history read never reached storage.");
        }

        public override IMonitoringApi GetMonitoringApi()
        {
            var proxy = DispatchProxy.Create<IMonitoringApi, StallingMonitoringApi>();
            ((StallingMonitoringApi)(object)proxy).Init(inner.GetMonitoringApi(), this);
            return proxy;
        }

        public override IStorageConnection GetConnection() => inner.GetConnection();

        public override IStorageConnection GetReadOnlyConnection() => inner.GetReadOnlyConnection();

        public override bool LinearizableReads => inner.LinearizableReads;

        public override bool HasFeature(string featureId) => inner.HasFeature(featureId);

        internal void OnHistoryRead()
        {
            if (_open.IsSet) return;
            _stalled.Set();
            _open.Wait();
        }
    }

    public class StallingMonitoringApi : DispatchProxy
    {
        private IMonitoringApi _inner;
        private StallingStorage _storage;

        internal void Init(IMonitoringApi inner, StallingStorage storage)
        {
            _inner = inner;
            _storage = storage;
        }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            if (targetMethod.Name == nameof(IMonitoringApi.HourlySucceededJobs))
                _storage.OnHistoryRead();
            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        }
    }
}
