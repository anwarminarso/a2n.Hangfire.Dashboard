using System;
using System.Collections.Generic;

namespace a2n.Hangfire.Dashboard.RestApi.Models;

/// <summary>
/// Serialization-friendly projection of a Hangfire queue, returned by <c>GET /queues</c>.
/// Only the scalar fields are exposed; the monitoring API's <c>FirstJobs</c> preview list is
/// intentionally omitted so the response stays a lightweight, read-only summary.
/// </summary>
/// <param name="Name">The queue name.</param>
/// <param name="Length">The number of enqueued jobs currently in the queue.</param>
/// <param name="Fetched">The number of fetched (in-flight) jobs, when the storage reports it.</param>
public sealed record QueueRecordDto(string Name, long Length, long? Fetched);

/// <summary>
/// A single state-history entry for a job, returned as part of <see cref="JobDetailsResponse"/>.
/// </summary>
/// <param name="StateName">The state name (e.g. Enqueued, Processing, Succeeded, Failed).</param>
/// <param name="Reason">The reason recorded for the transition into this state, if any.</param>
/// <param name="CreatedAt">When the job entered this state.</param>
/// <param name="Data">The state-specific data bag recorded for this transition.</param>
public sealed record JobStateHistoryDto(
    string? StateName,
    string? Reason,
    DateTime CreatedAt,
    IReadOnlyDictionary<string, string>? Data);

/// <summary>
/// Serialization-friendly projection of a single job's details, returned by <c>GET /jobs/{id}</c>.
/// Projected from the core <c>HangfireMonitorService.GetJobDetails</c> result so no storage-specific
/// query is issued and no non-serializable monitoring types leak into the JSON response.
/// </summary>
/// <param name="JobId">The unique job identifier.</param>
/// <param name="JobName">The resolved job display name (type and method), when the job could be loaded.</param>
/// <param name="State">The current state name (the most recent history entry).</param>
/// <param name="CreatedAt">When the job was created.</param>
/// <param name="ExpireAt">When the job's storage entry expires, when applicable.</param>
/// <param name="Properties">The job parameters/properties bag.</param>
/// <param name="History">The ordered state-history entries (most recent first).</param>
public sealed record JobDetailsResponse(
    string JobId,
    string? JobName,
    string? State,
    DateTime? CreatedAt,
    DateTime? ExpireAt,
    IReadOnlyDictionary<string, string>? Properties,
    IReadOnlyList<JobStateHistoryDto> History);
