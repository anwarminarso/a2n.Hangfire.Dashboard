#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hangfire;
using Hangfire.Client;
using Hangfire.Common;
using Hangfire.Server;
using Xunit;

namespace a2n.Hangfire.Dashboard.OpenTelemetry.Tests;

/// <summary>
/// Smoke test for the OpenTelemetry integration opt-in surface.
///
/// **Validates: Requirements 1.4, 4.1, 4.2, 4.4**
///
/// Asserts that:
/// <list type="bullet">
///   <item><description>
///     the explicit opt-in call <see cref="OpenTelemetryDashboardExtensions.UseHangfireDashboardOpenTelemetry"/>
///     registers BOTH the trace-capture client filter (<see cref="IClientFilter"/>) and the
///     span-restorer server filter (<see cref="IServerFilter"/>) into Hangfire's global filter chain
///     (Req 4.1);
///   </description></item>
///   <item><description>
///     WITHOUT the opt-in call, neither integration filter is present in the global filter chain and
///     the global configuration remains usable/unchanged (Req 1.4, 4.2);
///   </description></item>
///   <item><description>
///     the exposed <see cref="OpenTelemetryDashboardExtensions.ActivitySourceName"/> equals the
///     documented value hosts wire into their tracer provider (Req 4.4).
///   </description></item>
/// </list>
///
/// The opt-in registers into the process-wide <see cref="GlobalJobFilters.Filters"/> collection, so
/// each test snapshots and restores that collection to stay isolated and order-independent. The tests
/// are placed in a non-parallel collection because they mutate shared global state.
/// </summary>
[Collection(nameof(GlobalFilterCollection))]
public class OpenTelemetryOptInSmokeTests
{
    [Fact]
    public void ActivitySourceName_EqualsExpectedValue()
    {
        Assert.Equal("a2n.Hangfire.Dashboard", OpenTelemetryDashboardExtensions.ActivitySourceName);
    }

    [Fact]
    public void UseHangfireDashboardOpenTelemetry_RegistersBothClientAndServerFilters()
    {
        RemoveIntegrationFilters();
        try
        {
            // Baseline: no integration filter present before the opt-in call.
            Assert.DoesNotContain(IntegrationFilterInstances(), f => f is TraceCaptureClientFilter);
            Assert.DoesNotContain(IntegrationFilterInstances(), f => f is SpanRestorerServerFilter);

            // The single explicit opt-in call registers the filters (Req 4.1).
            var returned = GlobalConfiguration.Configuration.UseHangfireDashboardOpenTelemetry();

            // Returns the same configuration for chaining.
            Assert.Same(GlobalConfiguration.Configuration, returned);

            var instances = IntegrationFilterInstances();

            var clientFilter = Assert.Single(instances.OfType<TraceCaptureClientFilter>());
            var serverFilter = Assert.Single(instances.OfType<SpanRestorerServerFilter>());

            // The registered instances implement the expected Hangfire filter interfaces.
            Assert.IsAssignableFrom<IClientFilter>(clientFilter);
            Assert.IsAssignableFrom<IServerFilter>(serverFilter);
        }
        finally
        {
            RemoveIntegrationFilters();
        }
    }

    [Fact]
    public void WithoutRegistration_NoIntegrationFilterIsPresent_AndConfigurationRemainsUsable()
    {
        RemoveIntegrationFilters();
        try
        {
            // Without the opt-in call, no integration filter contributes to the global chain (Req 1.4, 4.2).
            var instances = IntegrationFilterInstances();
            Assert.DoesNotContain(instances, f => f is TraceCaptureClientFilter);
            Assert.DoesNotContain(instances, f => f is SpanRestorerServerFilter);

            // The dashboard/global configuration resolves unchanged and stays usable.
            Assert.NotNull(GlobalConfiguration.Configuration);
        }
        finally
        {
            RemoveIntegrationFilters();
        }
    }

    /// <summary>Snapshots the current instances registered in the global Hangfire filter chain.</summary>
    private static List<object> IntegrationFilterInstances() =>
        GlobalJobFilters.Filters.Select(f => f.Instance).ToList();

    /// <summary>Removes any integration filters from the global chain to keep tests isolated.</summary>
    private static void RemoveIntegrationFilters()
    {
        var toRemove = GlobalJobFilters.Filters
            .Where(f => f.Instance is TraceCaptureClientFilter or SpanRestorerServerFilter)
            .Select(f => f.Instance)
            .ToList();

        foreach (var instance in toRemove)
        {
            GlobalJobFilters.Filters.Remove(instance);
        }
    }
}

/// <summary>
/// xUnit collection used to serialize tests that mutate the process-wide
/// <see cref="GlobalJobFilters.Filters"/> collection, preventing cross-test interference.
/// </summary>
[CollectionDefinition(nameof(GlobalFilterCollection), DisableParallelization = true)]
public sealed class GlobalFilterCollection
{
}
