#nullable enable

using System;
using System.Diagnostics;
using a2n.Hangfire.Dashboard.Internal;
using Hangfire.Logging;
using Hangfire.Server;

namespace a2n.Hangfire.Dashboard.OpenTelemetry;

/// <summary>
/// Hangfire server filter that restores the captured enqueue trace context as a child execution span
/// on a named <see cref="ActivitySource"/>, and records the terminal job outcome as the span status.
/// </summary>
/// <remarks>
/// <para>
/// On <see cref="OnPerforming"/> the filter reads the stored W3C <c>traceparent</c> job parameter:
/// </para>
/// <list type="bullet">
///   <item><description>valid — starts an execution span linked to the parsed parent trace context (Req 2.1);</description></item>
///   <item><description>absent — starts a parentless execution span without raising an error (Req 2.3);</description></item>
///   <item><description>malformed — starts a parentless execution span and writes a diagnostic log entry (Req 2.4).</description></item>
/// </list>
/// <para>
/// On <see cref="OnPerformed"/> the started span's status is set from the terminal outcome
/// (<see cref="ActivityStatusCode.Ok"/> when no exception occurred, otherwise
/// <see cref="ActivityStatusCode.Error"/>) and the span is ended (Req 2.2).
/// </para>
/// </remarks>
public sealed class SpanRestorerServerFilter : IServerFilter
{
    /// <summary>
    /// The name given to job execution spans started by this filter.
    /// </summary>
    internal const string ActivityName = "hangfire.job.execute";

    /// <summary>
    /// The <see cref="PerformContext.Items"/> key under which the started <see cref="Activity"/> is
    /// stashed between <see cref="OnPerforming"/> and <see cref="OnPerformed"/>.
    /// </summary>
    internal const string ActivityItemKey = "a2n.Hangfire.Dashboard.OpenTelemetry.Activity";

    private static readonly ILog Logger = LogProvider.GetLogger(typeof(SpanRestorerServerFilter));

    private readonly string _traceParentParameterName;

    /// <summary>
    /// Initializes a new instance of the <see cref="SpanRestorerServerFilter"/> class.
    /// </summary>
    /// <param name="traceParentParameterName">
    /// The Hangfire job parameter name from which the captured <c>traceparent</c> is read.
    /// </param>
    public SpanRestorerServerFilter(string traceParentParameterName)
    {
        _traceParentParameterName = traceParentParameterName;
    }

    /// <inheritdoc />
    public void OnPerforming(PerformingContext context)
    {
        if (context is null)
        {
            return;
        }

        var traceParent = ReadStoredTraceParent(context);

        Activity? activity;
        if (traceParent is null)
        {
            // No stored traceparent: start a parentless span (Req 2.3).
            activity = OpenTelemetryDashboardExtensions.ActivitySource.StartActivity(
                GetSpanName(context), ActivityKind.Consumer);
        }
        else if (W3CTraceParent.TryParse(traceParent, out var parsed))
        {
            // Valid traceparent: link the span to the stored parent trace context (Req 2.1).
            var parentContext = new ActivityContext(
                ActivityTraceId.CreateFromString(parsed.TraceId.AsSpan()),
                ActivitySpanId.CreateFromString(parsed.ParentId.AsSpan()),
                (ActivityTraceFlags)parsed.TraceFlags,
                traceState: null,
                isRemote: true);

            activity = OpenTelemetryDashboardExtensions.ActivitySource.StartActivity(
                GetSpanName(context), ActivityKind.Consumer, parentContext);
        }
        else
        {
            // Malformed traceparent: still execute normally with a parentless span, and log (Req 2.4).
            Logger.WarnFormat(
                "Ignoring malformed stored traceparent '{0}' on job parameter '{1}'; starting a parentless execution span.",
                traceParent,
                _traceParentParameterName);

            activity = OpenTelemetryDashboardExtensions.ActivitySource.StartActivity(
                GetSpanName(context), ActivityKind.Consumer);
        }

        // Stash the started activity (may be null when no listener is registered) so OnPerformed can
        // complete it. Store only when non-null to keep the Items bag clean.
        if (activity is not null)
        {
            context.Items[ActivityItemKey] = activity;
        }
    }

    /// <inheritdoc />
    public void OnPerformed(PerformedContext context)
    {
        if (context is null)
        {
            return;
        }

        if (!context.Items.TryGetValue(ActivityItemKey, out var stored) || stored is not Activity activity)
        {
            return;
        }

        // Record the terminal job outcome as the span status (Req 2.2).
        if (context.Exception is null)
        {
            activity.SetStatus(ActivityStatusCode.Ok);
        }
        else
        {
            activity.SetStatus(ActivityStatusCode.Error, context.Exception.Message);
        }

        activity.Dispose();
    }

    private string? ReadStoredTraceParent(PerformingContext context)
    {
        try
        {
            return context.GetJobParameter<string>(_traceParentParameterName);
        }
        catch (Exception ex)
        {
            // Never let trace restoration interfere with job execution.
            Logger.WarnFormat(
                "Failed to read stored traceparent from job parameter '{0}': {1}. Starting a parentless execution span.",
                _traceParentParameterName,
                ex.Message);
            return null;
        }
    }

    private static string GetSpanName(PerformContext context)
    {
        var job = context.BackgroundJob?.Job;
        if (job?.Type is not null && job.Method is not null)
        {
            return $"{job.Type.Name}.{job.Method.Name}";
        }

        return ActivityName;
    }
}
