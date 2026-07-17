#nullable enable
using System;

namespace a2n.Hangfire.Dashboard.Models;

/// <summary>
/// Shared, serialization-friendly job record used by both the read-only REST API JSON
/// responses and the JSON export. Projected from <see cref="JobSummaryDto"/> so the two
/// surfaces expose an identical shape and never drift (Req 9.3 / 13.3).
/// </summary>
/// <param name="JobId">The unique job identifier.</param>
/// <param name="JobName">The display name of the job (type and method).</param>
/// <param name="State">The current job state (e.g. Succeeded, Failed, Processing).</param>
/// <param name="Queue">The queue the job was enqueued to.</param>
/// <param name="CreatedAt">When the job was created.</param>
/// <param name="LastStateChange">When the job last changed state.</param>
/// <param name="DurationMs">Processing duration in milliseconds.</param>
/// <param name="LatencyMs">Queue wait latency in milliseconds.</param>
/// <param name="Tags">Tags associated with the job.</param>
/// <param name="ExceptionType">The exception type if the job failed.</param>
/// <param name="ExceptionMessage">The exception message if the job failed.</param>
public sealed record JobRecordDto(
    string JobId,
    string JobName,
    string State,
    string Queue,
    DateTime? CreatedAt,
    DateTime? LastStateChange,
    double? DurationMs,
    double? LatencyMs,
    string[]? Tags,
    string? ExceptionType,
    string? ExceptionMessage);
