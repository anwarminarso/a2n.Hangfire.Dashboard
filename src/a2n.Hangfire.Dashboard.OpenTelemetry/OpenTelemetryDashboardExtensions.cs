#nullable enable

using System;
using System.Diagnostics;
using Hangfire;

namespace a2n.Hangfire.Dashboard.OpenTelemetry;

/// <summary>
/// Opt-in registration entry point for the a2n.Hangfire.Dashboard OpenTelemetry trace-linking
/// integration. Registers the trace-capture client filter and the span-restorer server filter into
/// Hangfire's global filter chain and exposes the <see cref="ActivitySourceName"/> that host
/// applications add to their OpenTelemetry <c>TracerProviderBuilder</c>.
/// </summary>
public static class OpenTelemetryDashboardExtensions
{
    /// <summary>
    /// The <see cref="ActivitySource"/> name that host applications register with their existing
    /// OpenTelemetry tracer provider so job execution spans are collected (Req 4.4).
    /// </summary>
    public const string ActivitySourceName = "a2n.Hangfire.Dashboard";

    /// <summary>
    /// The shared <see cref="ActivitySource"/> used by the integration to emit job execution spans.
    /// </summary>
    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    /// <summary>
    /// Enables OpenTelemetry trace linking for Hangfire jobs by registering the trace-capture client
    /// filter and the span-restorer server filter. This is the single explicit opt-in call; when it is
    /// not invoked, the dashboard enqueues and executes jobs without storing a traceparent and all
    /// other features are unchanged (Req 4.1, 4.2).
    /// </summary>
    /// <param name="config">The Hangfire global configuration to register the filters into.</param>
    /// <param name="configure">An optional callback to customize <see cref="OpenTelemetryIntegrationOptions"/>.</param>
    /// <returns>The same <see cref="IGlobalConfiguration"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is <see langword="null"/>.</exception>
    public static IGlobalConfiguration UseHangfireDashboardOpenTelemetry(
        this IGlobalConfiguration config,
        Action<OpenTelemetryIntegrationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var options = new OpenTelemetryIntegrationOptions();
        configure?.Invoke(options);

        var parameterName = string.IsNullOrWhiteSpace(options.TraceParentParameterName)
            ? OpenTelemetryIntegrationOptions.DefaultTraceParentParameterName
            : options.TraceParentParameterName;

        config.UseFilter(new TraceCaptureClientFilter(parameterName));
        config.UseFilter(new SpanRestorerServerFilter(parameterName));

        return config;
    }
}
