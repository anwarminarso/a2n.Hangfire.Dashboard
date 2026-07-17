using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.RestApi.Models;
using a2n.Hangfire.Dashboard.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace a2n.Hangfire.Dashboard.RestApi.Endpoints;

/// <summary>
/// Maps the read-only REST API endpoints onto the <c>{pathPrefix}/api/v1</c> route group.
/// </summary>
/// <remarks>
/// Every endpoint is GET and read-only. Job data is obtained exclusively through the existing
/// <see cref="IStorageQueryProvider"/>, the optional <see cref="IStorageMetricsProvider"/>, and the
/// core <see cref="HangfireMonitorService"/> (which wraps <c>IMonitoringApi</c>) — no new
/// storage-specific query is issued (Req 9.2). Invalid query parameters produce an HTTP 400
/// problem-details response with no job data (Req 9.5). Metrics-backed endpoints degrade to HTTP 404
/// when no <see cref="IStorageMetricsProvider"/> is registered, while the query-backed endpoints stay
/// available (Req 9.6).
/// </remarks>
internal static class RestApiEndpoints
{
    // The canonical Hangfire job states accepted by GET /jobs/state/{state}. Matching is
    // case-insensitive; anything outside this set is an unknown state and yields HTTP 400 (Req 9.5).
    private static readonly HashSet<string> KnownStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "Enqueued", "Scheduled", "Processing", "Succeeded", "Failed", "Deleted", "Awaiting",
    };

    /// <summary>
    /// Registers all read-only endpoints onto the supplied route group.
    /// </summary>
    public static void Map(RouteGroupBuilder group, RestApiOptions options)
    {
        // GET /jobs — search/filter + paging via IStorageQueryProvider.GetJobsWithFilterAsync (Req 9.1, 9.3).
        group.MapGet("/jobs", async (
            HttpContext context,
            IStorageQueryProvider queryProvider,
            CancellationToken ct) =>
        {
            if (!TryResolvePaging(context.Request, options, out var page, out var pageSize, out var pagingError))
                return pagingError!;

            if (!TryBuildCriteria(context.Request, out var criteria, out var criteriaError))
                return criteriaError!;

            var paged = await queryProvider.GetJobsWithFilterAsync(criteria, page, pageSize, ct).ConfigureAwait(false);
            return Results.Ok(paged.ToResponse());
        })
        .WithName("SearchJobs")
        .WithTags("Jobs")
        .Produces<PagedResponse<JobRecordDto>>(StatusCodes.Status200OK)
        .ProducesValidationProblem();

        // GET /jobs/state/{state} — list by state + paging via IStorageQueryProvider.GetJobsByStateAsync.
        group.MapGet("/jobs/state/{state}", async (
            string state,
            HttpContext context,
            IStorageQueryProvider queryProvider,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(state) || !KnownStates.Contains(state.Trim()))
            {
                return ValidationError(
                    "state",
                    $"Unknown job state '{state}'. Valid states are: {string.Join(", ", KnownStates.OrderBy(s => s))}.");
            }

            if (!TryResolvePaging(context.Request, options, out var page, out var pageSize, out var pagingError))
                return pagingError!;

            var paged = await queryProvider.GetJobsByStateAsync(state.Trim(), page, pageSize, ct).ConfigureAwait(false);
            return Results.Ok(paged.ToResponse());
        })
        .WithName("GetJobsByState")
        .WithTags("Jobs")
        .Produces<PagedResponse<JobRecordDto>>(StatusCodes.Status200OK)
        .ProducesValidationProblem();

        // GET /jobs/{id} — job details via HangfireMonitorService.GetJobDetails / IMonitoringApi.
        group.MapGet("/jobs/{id}", (
            string id,
            HangfireMonitorService monitor) =>
        {
            if (string.IsNullOrWhiteSpace(id))
                return ValidationError("id", "'id' must be a non-empty job identifier.");

            var details = monitor.GetJobDetails(id);
            if (details is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Job not found", detail: $"No job with id '{id}' was found.");

            return Results.Ok(ProjectJobDetails(id, details));
        })
        .WithName("GetJobDetails")
        .WithTags("Jobs")
        .Produces<JobDetailsResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem();

        // GET /queues — list queues via IMonitoringApi.Queues() (through HangfireMonitorService).
        group.MapGet("/queues", (HangfireMonitorService monitor) =>
        {
            var queues = monitor.GetQueues() ?? new List<global::Hangfire.Storage.Monitoring.QueueWithTopEnqueuedJobsDto>();
            var records = queues
                .Select(q => new QueueRecordDto(q.Name, q.Length, q.Fetched))
                .ToList();
            return Results.Ok(records);
        })
        .WithName("GetQueues")
        .WithTags("Queues")
        .Produces<IReadOnlyList<QueueRecordDto>>(StatusCodes.Status200OK);

        MapMetricsEndpoints(group);
    }

    // GET /metrics/* — backed by IStorageMetricsProvider. When the provider is not registered the
    // endpoints return HTTP 404 "not available" while every query-backed endpoint stays up (Req 9.6).
    private static void MapMetricsEndpoints(RouteGroupBuilder group)
    {
        // GET /metrics/job-duration — average/min/max/percentile duration stats per job type.
        group.MapGet("/metrics/job-duration", async (
            HttpContext context,
            CancellationToken ct) =>
        {
            var provider = context.RequestServices.GetService<IStorageMetricsProvider>();
            if (provider is null)
                return MetricsNotAvailable();

            if (!TryResolveRange(context.Request, out var from, out var to, out var rangeError))
                return rangeError!;

            var stats = await provider.GetJobDurationStatsAsync(from, to, ct).ConfigureAwait(false);
            return Results.Ok(stats);
        })
        .WithName("GetJobDurationMetrics")
        .WithTags("Metrics")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .ProducesValidationProblem();

        // GET /metrics/queue-latency — queue wait-time percentiles per queue.
        group.MapGet("/metrics/queue-latency", async (
            HttpContext context,
            CancellationToken ct) =>
        {
            var provider = context.RequestServices.GetService<IStorageMetricsProvider>();
            if (provider is null)
                return MetricsNotAvailable();

            if (!TryResolveRange(context.Request, out var from, out var to, out var rangeError))
                return rangeError!;

            var stats = await provider.GetQueueLatencyStatsAsync(from, to, ct).ConfigureAwait(false);
            return Results.Ok(stats);
        })
        .WithName("GetQueueLatencyMetrics")
        .WithTags("Metrics")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .ProducesValidationProblem();
    }

    private static IResult MetricsNotAvailable()
        => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Metrics not available",
            detail: "No IStorageMetricsProvider is registered, so metrics-backed endpoints are unavailable on this storage backend.");

    private static JobDetailsResponse ProjectJobDetails(
        string jobId, global::Hangfire.Storage.Monitoring.JobDetailsDto details)
    {
        string? jobName = null;
        if (details.Job is not null)
        {
            var typeName = details.Job.Type?.Name;
            var methodName = details.Job.Method?.Name;
            jobName = typeName is null
                ? methodName
                : methodName is null ? typeName : $"{typeName}.{methodName}";
        }

        var history = (details.History ?? new List<global::Hangfire.Storage.Monitoring.StateHistoryDto>())
            .Select(h => new JobStateHistoryDto(
                h.StateName,
                h.Reason,
                h.CreatedAt,
                h.Data is null ? null : new Dictionary<string, string>(h.Data)))
            .ToList();

        var state = history.Count > 0 ? history[0].StateName : null;

        IReadOnlyDictionary<string, string>? properties =
            details.Properties is null ? null : new Dictionary<string, string>(details.Properties);

        return new JobDetailsResponse(
            JobId: jobId,
            JobName: jobName,
            State: state,
            CreatedAt: details.CreatedAt,
            ExpireAt: details.ExpireAt,
            Properties: properties,
            History: history);
    }

    // ── Parameter parsing / validation helpers ───────────────────────────────────────────────

    private static bool TryResolvePaging(
        HttpRequest request, RestApiOptions options, out int page, out int pageSize, out IResult? error)
    {
        page = 1;
        pageSize = options.DefaultPageSize;
        error = null;

        if (TryGetRawValue(request.Query, "page", out var rawPage))
        {
            if (!int.TryParse(rawPage, NumberStyles.Integer, CultureInfo.InvariantCulture, out page) || page < 1)
            {
                error = ValidationError("page", "'page' must be an integer greater than or equal to 1.");
                return false;
            }
        }

        if (TryGetRawValue(request.Query, "pageSize", out var rawPageSize))
        {
            if (!int.TryParse(rawPageSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out pageSize)
                || pageSize < 1
                || pageSize > options.MaxPageSize)
            {
                error = ValidationError(
                    "pageSize",
                    $"'pageSize' must be an integer between 1 and {options.MaxPageSize}.");
                return false;
            }
        }

        return true;
    }

    private static bool TryResolveRange(
        HttpRequest request, out DateTimeOffset from, out DateTimeOffset to, out IResult? error)
    {
        error = null;
        to = DateTimeOffset.UtcNow;
        from = to.AddDays(-1);

        if (TryGetRawValue(request.Query, "from", out var rawFrom)
            && !TryParseDate(rawFrom, out from))
        {
            error = ValidationError("from", "'from' must be a valid ISO-8601 date/time.");
            return false;
        }

        if (TryGetRawValue(request.Query, "to", out var rawTo)
            && !TryParseDate(rawTo, out to))
        {
            error = ValidationError("to", "'to' must be a valid ISO-8601 date/time.");
            return false;
        }

        if (from > to)
        {
            error = ValidationError("from", "'from' must be earlier than or equal to 'to'.");
            return false;
        }

        return true;
    }

    private static bool TryBuildCriteria(
        HttpRequest request, out JobFilterCriteria criteria, out IResult? error)
    {
        criteria = new JobFilterCriteria();
        error = null;
        var query = request.Query;

        if (TryGetRawValue(query, "state", out var state))
        {
            if (!KnownStates.Contains(state.Trim()))
            {
                error = ValidationError(
                    "state",
                    $"Unknown job state '{state}'. Valid states are: {string.Join(", ", KnownStates.OrderBy(s => s))}.");
                return false;
            }

            criteria.State = state.Trim();
        }

        if (query.TryGetValue("states", out var states))
        {
            var list = states
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim())
                .ToList();

            var unknown = list.FirstOrDefault(s => !KnownStates.Contains(s));
            if (unknown is not null)
            {
                error = ValidationError(
                    "states",
                    $"Unknown job state '{unknown}'. Valid states are: {string.Join(", ", KnownStates.OrderBy(s => s))}.");
                return false;
            }

            if (list.Count > 0)
                criteria.States = list;
        }

        if (TryGetRawValue(query, "queue", out var queue))
            criteria.Queue = queue;

        if (TryGetRawValue(query, "server", out var server))
            criteria.Server = server;

        if (TryGetRawValue(query, "jobName", out var jobName))
            criteria.JobNamePattern = jobName;

        if (TryGetRawValue(query, "exception", out var exception))
            criteria.ExceptionPattern = exception;

        if (TryGetRawValue(query, "content", out var content))
            criteria.ContentPattern = content;

        if (query.TryGetValue("tag", out var tags))
        {
            var list = tags
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t!.Trim())
                .ToList();
            if (list.Count > 0)
                criteria.Tags = list;
        }

        if (TryGetRawValue(query, "recurringJobId", out var recurringJobId))
            criteria.RecurringJobId = recurringJobId;

        if (TryGetRawValue(query, "dateFrom", out var rawDateFrom))
        {
            if (!TryParseDate(rawDateFrom, out var dateFrom))
            {
                error = ValidationError("dateFrom", "'dateFrom' must be a valid ISO-8601 date/time.");
                return false;
            }

            criteria.DateFrom = dateFrom;
        }

        if (TryGetRawValue(query, "dateTo", out var rawDateTo))
        {
            if (!TryParseDate(rawDateTo, out var dateTo))
            {
                error = ValidationError("dateTo", "'dateTo' must be a valid ISO-8601 date/time.");
                return false;
            }

            criteria.DateTo = dateTo;
        }

        if (criteria.DateFrom.HasValue && criteria.DateTo.HasValue && criteria.DateFrom > criteria.DateTo)
        {
            error = ValidationError("dateFrom", "'dateFrom' must be earlier than or equal to 'dateTo'.");
            return false;
        }

        return true;
    }

    private static bool TryGetRawValue(IQueryCollection query, string key, out string value)
    {
        value = string.Empty;
        if (!query.TryGetValue(key, out var values) || values.Count == 0)
            return false;

        var raw = values[values.Count - 1];
        if (string.IsNullOrEmpty(raw))
            return false;

        value = raw!;
        return true;
    }

    private static bool TryParseDate(string raw, out DateTimeOffset value)
        => DateTimeOffset.TryParse(
            raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out value);

    private static IResult ValidationError(string key, string message)
        => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [key] = new[] { message },
        });
}
