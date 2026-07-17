#nullable enable
using System.Globalization;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services.Export;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace a2n.Hangfire.Dashboard.Middleware;

/// <summary>
/// Serves the CSV / JSON job export endpoint (default path <c>/export</c>, relative to the
/// dashboard's configured <c>Path_Prefix</c>) within the dashboard's branched pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="HealthCheckEndpoint"/> / <see cref="PrometheusMetricsEndpoint"/>: it is invoked
/// by path match from <see cref="DashboardMiddleware"/> before Blazor routing so it automatically
/// inherits the configured <c>Path_Prefix</c> (Req 16.1).
/// </para>
/// <para>
/// Unlike the Prometheus endpoint (which applies its own <c>LocalOnly</c> mode), the export endpoint
/// is gated by <c>Dashboard_Authorization</c> so it cannot be used to bypass dashboard access control.
/// Authorization is enforced <em>before</em> any record is streamed; on failure the endpoint responds
/// with HTTP 401 and an empty body — no records are emitted (Req 14.1, 14.2, 17.1, 17.2).
/// </para>
/// <para>
/// Export is a read operation, so it remains available even when the dashboard is in read-only mode:
/// the endpoint does not consult <c>IsReadOnly</c> (Req 14.3).
/// </para>
/// </remarks>
internal static class ExportEndpoint
{
    private const string CsvContentType = "text/csv; charset=utf-8";
    private const string JsonContentType = "application/json; charset=utf-8";

    // Canonical Hangfire job states accepted for the state/states filters, matching the REST API
    // conventions. Matching is case-insensitive; anything outside this set is an unknown state.
    private static readonly HashSet<string> KnownStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "Enqueued", "Scheduled", "Processing", "Succeeded", "Failed", "Deleted", "Awaiting",
    };

    /// <summary>
    /// Returns true and writes a response if the request matched the export endpoint;
    /// otherwise returns false so the caller can continue processing.
    /// </summary>
    public static async Task<bool> TryHandleAsync(HttpContext context, DashboardUIOptions options)
    {
        var export = options.Export;

        // Not enabled → not handled (Req 15.2).
        if (export is null || !export.Enabled)
            return false;

        var configuredPath = string.IsNullOrEmpty(export.Path) ? "/export" : export.Path;
        var path = context.Request.Path.Value ?? string.Empty;

        // Exact match (case-insensitive), tolerating a trailing slash — mirrors PrometheusMetricsEndpoint.
        if (!IsPathMatch(path, configuredPath))
            return false;

        // Authorization gate — enforced BEFORE any record is streamed (Req 14.1, 14.2, 17.1).
        if (!await Security.DashboardAuthorization.IsAuthorizedAsync(context, options, context.RequestAborted)
                .ConfigureAwait(false))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return true; // 401 with no body — no records emitted (Req 14.2, 17.2).
        }

        // Parse the requested format: csv (default when absent) or json; unknown → 400 (Req 13.5).
        var format = (context.Request.Query["format"].ToString() ?? string.Empty).Trim();
        var isJson = false;
        if (string.IsNullOrEmpty(format) || string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            isJson = false;
        }
        else if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            isJson = true;
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(
                $"Unknown export format '{format}'. Valid formats are: csv, json.", context.RequestAborted)
                .ConfigureAwait(false);
            return true;
        }

        // Parse the current Search_Criteria from the query string (Req 13.1, 13.6).
        if (!TryBuildCriteria(context.Request, out var criteria, out var criteriaError))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(criteriaError ?? "Invalid export criteria.", context.RequestAborted)
                .ConfigureAwait(false);
            return true;
        }

        // Resolve the query provider from request services and construct the export service.
        var queryProvider = context.RequestServices.GetService<IStorageQueryProvider>();
        if (queryProvider is null)
        {
            var logger = context.RequestServices.GetService<ILoggerFactory>()?
                .CreateLogger("a2n.Hangfire.Dashboard.Export");
            logger?.LogError("Job export requested but no IStorageQueryProvider is registered.");
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return true;
        }

        var service = new JobExportService(queryProvider);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var extension = isJson ? "json" : "csv";
        var fileName = $"jobs-export-{timestamp}.{extension}";

        // Content-Disposition attachment + per-format content type set BEFORE streaming (Req 13.5).
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = isJson ? JsonContentType : CsvContentType;
        context.Response.Headers["Content-Disposition"] = $"attachment; filename={fileName}";

        if (isJson)
            await service.WriteJsonAsync(context.Response.Body, criteria, export.MaxRecords, context.RequestAborted)
                .ConfigureAwait(false);
        else
            await service.WriteCsvAsync(context.Response.Body, criteria, export.MaxRecords, context.RequestAborted)
                .ConfigureAwait(false);

        return true;
    }

    private static bool IsPathMatch(string requestPath, string configuredPath)
    {
        if (string.Equals(requestPath, configuredPath, StringComparison.OrdinalIgnoreCase))
            return true;

        // Tolerate a trailing slash in either direction.
        var trimmedRequest = requestPath.TrimEnd('/');
        var trimmedConfigured = configuredPath.TrimEnd('/');
        return trimmedConfigured.Length > 0
            && string.Equals(trimmedRequest, trimmedConfigured, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds a <see cref="JobFilterCriteria"/> from the query string, reusing the same parameter
    /// names as the read-only REST API (<c>state</c>, <c>states</c>, <c>queue</c>, <c>server</c>,
    /// <c>jobName</c>, <c>exception</c>, <c>content</c>, <c>tag</c>, <c>recurringJobId</c>,
    /// <c>dateFrom</c>, <c>dateTo</c>) so both surfaces stay consistent.
    /// </summary>
    private static bool TryBuildCriteria(HttpRequest request, out JobFilterCriteria criteria, out string? error)
    {
        criteria = new JobFilterCriteria();
        error = null;
        var query = request.Query;

        if (TryGetRawValue(query, "state", out var state))
        {
            if (!KnownStates.Contains(state.Trim()))
            {
                error = $"Unknown job state '{state}'. Valid states are: {string.Join(", ", KnownStates.OrderBy(s => s))}.";
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
                error = $"Unknown job state '{unknown}'. Valid states are: {string.Join(", ", KnownStates.OrderBy(s => s))}.";
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
                error = "'dateFrom' must be a valid ISO-8601 date/time.";
                return false;
            }

            criteria.DateFrom = dateFrom;
        }

        if (TryGetRawValue(query, "dateTo", out var rawDateTo))
        {
            if (!TryParseDate(rawDateTo, out var dateTo))
            {
                error = "'dateTo' must be a valid ISO-8601 date/time.";
                return false;
            }

            criteria.DateTo = dateTo;
        }

        if (criteria.DateFrom.HasValue && criteria.DateTo.HasValue && criteria.DateFrom > criteria.DateTo)
        {
            error = "'dateFrom' must be earlier than or equal to 'dateTo'.";
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
}
