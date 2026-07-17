#nullable enable
namespace a2n.Hangfire.Dashboard.Models;

/// <summary>
/// The captured distributed-trace context for a job, passed to a
/// <see cref="DashboardUIOptions.TraceLinkBuilder"/> so the Job Details page can render a
/// "View distributed trace →" deep link into an external tracing backend (Req 3).
/// </summary>
/// <param name="Traceparent">The raw W3C Trace Context <c>traceparent</c> string.</param>
/// <param name="TraceId">The 32-hex-char trace-id extracted from the traceparent.</param>
/// <param name="ParentSpanId">The 16-hex-char parent span-id extracted from the traceparent.</param>
public sealed record TraceLinkContext(string Traceparent, string TraceId, string ParentSpanId);
