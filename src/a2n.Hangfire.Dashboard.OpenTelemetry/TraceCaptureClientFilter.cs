#nullable enable

using System.Diagnostics;
using a2n.Hangfire.Dashboard.Internal;
using Hangfire.Client;
using Hangfire.Common;

namespace a2n.Hangfire.Dashboard.OpenTelemetry;

/// <summary>
/// Hangfire client filter that captures the ambient W3C <c>traceparent</c> when a job is enqueued and
/// stores it as a job parameter, so the enqueue trace context can be restored on execute.
/// </summary>
/// <remarks>
/// This is a scaffold registered by <see cref="OpenTelemetryDashboardExtensions"/>. The capture logic
/// is implemented in a later task; the filter is currently a no-op that never throws (Req 1.2).
/// </remarks>
public sealed class TraceCaptureClientFilter : IClientFilter
{
    private readonly string _traceParentParameterName;

    /// <summary>
    /// Initializes a new instance of the <see cref="TraceCaptureClientFilter"/> class.
    /// </summary>
    /// <param name="traceParentParameterName">
    /// The Hangfire job parameter name under which the captured <c>traceparent</c> is stored.
    /// </param>
    public TraceCaptureClientFilter(string traceParentParameterName)
    {
        _traceParentParameterName = traceParentParameterName;
    }

    /// <inheritdoc />
    public void OnCreating(CreatingContext context)
    {
        // Only capture when there is an ambient trace context. With no active Activity we store
        // nothing and never throw, so enqueue behaves exactly as it would without this filter (Req 1.2).
        var activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        // Build a canonical W3C traceparent from the ambient activity's components. The current
        // activity's span-id becomes the parent span-id of the eventual execution span, so the
        // enqueue-to-execute path can later be stitched into a single trace. Storing the W3C string
        // keeps the trace-id and parent span-id recoverable and round-trippable via
        // W3CTraceParent.TryParse (Req 1.1, 1.3).
        var traceParent = new W3CTraceParent(
            Version: 0,
            TraceId: activity.TraceId.ToHexString(),
            ParentId: activity.SpanId.ToHexString(),
            TraceFlags: (byte)activity.ActivityTraceFlags);

        context.SetJobParameter(_traceParentParameterName, traceParent.ToString());
    }

    /// <inheritdoc />
    public void OnCreated(CreatedContext context)
    {
        // No-op.
    }
}
