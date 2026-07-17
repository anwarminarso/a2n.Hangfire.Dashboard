#nullable enable

namespace a2n.Hangfire.Dashboard.OpenTelemetry;

/// <summary>
/// Configuration knobs for the a2n.Hangfire.Dashboard OpenTelemetry trace-linking integration.
/// </summary>
/// <remarks>
/// Reserved for future options. The only knob today is the Hangfire job parameter name under which
/// the captured W3C <c>traceparent</c> is stored on enqueue and read back on execute; the trace
/// capture and span restore filters must agree on this name.
/// </remarks>
public sealed class OpenTelemetryIntegrationOptions
{
    /// <summary>
    /// The default Hangfire job parameter name used to store the captured W3C <c>traceparent</c>.
    /// </summary>
    public const string DefaultTraceParentParameterName = "otel.traceparent";

    /// <summary>
    /// The Hangfire job parameter name under which the captured W3C <c>traceparent</c> is stored on
    /// enqueue and read back on execute. Defaults to <c>otel.traceparent</c>.
    /// </summary>
    public string TraceParentParameterName { get; set; } = DefaultTraceParentParameterName;
}
