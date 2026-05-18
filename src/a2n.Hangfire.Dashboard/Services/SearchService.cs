using System.Diagnostics;
using Hangfire;
using Hangfire.Storage;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using Microsoft.Extensions.Logging;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Orchestrates job search queries across Hangfire storage.
/// Builds a unified <see cref="JobFilterCriteria"/> from the <see cref="SearchRequest"/>
/// and delegates to <see cref="IStorageQueryProvider"/> for database-level execution.
/// </summary>
public class SearchService
{
    private const int MaxQueryLength = 200;
    private const int MinNameLength = 2;
    private const int MaxIdLength = 20;

    private readonly JobStorage _storage;
    private readonly TagsDataReader _tagsReader;
    private readonly IStorageQueryProvider _queryProvider;
    private readonly bool _hasDedicatedProvider;
    private readonly ILogger<SearchService> _logger;

    public SearchService(JobStorage storage, TagsDataReader tagsReader, IStorageQueryProvider queryProvider = null,
        ILogger<SearchService> logger = null)
    {
        _storage = storage;
        _tagsReader = tagsReader;
        _queryProvider = queryProvider;
        _hasDedicatedProvider = queryProvider != null && queryProvider is not GenericQueryProvider;
        _logger = logger;
    }

    /// <summary>
    /// Executes a search with the given request parameters.
    /// Detects search mode, builds unified criteria, and delegates to the query provider.
    /// </summary>
    public async Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var (mode, normalizedQuery) = DetectSearchMode(request.Query);

        // If mode was explicitly set on the request, use that instead of auto-detection
        if (request.Mode != SearchMode.Auto)
        {
            mode = request.Mode;
            normalizedQuery = request.Query?.Trim() ?? "";
        }

        // Special case: ID lookup (single job, direct fetch — no filter needed)
        if (mode == SearchMode.Id)
        {
            var idResult = SearchByIdDirect(normalizedQuery);
            sw.Stop();
            idResult.Elapsed = sw.Elapsed;
            return idResult;
        }

        // Build unified criteria from request + detected mode
        var criteria = BuildCriteria(request, mode, normalizedQuery);

        // If no criteria at all, return empty
        if (!criteria.HasAnyCriteria())
        {
            sw.Stop();
            return new SearchResult { Elapsed = sw.Elapsed };
        }

        // Calculate pagination
        var page = (request.From / Math.Max(request.PageSize, 1)) + 1;
        var pageSize = Math.Min(request.PageSize, 50);

        try
        {
            // All searches go through unified filter
            var pagedResult = await _queryProvider.GetJobsWithFilterAsync(criteria, page, pageSize, ct);
            sw.Stop();

            var searchResult = ConvertPagedResult(pagedResult, DetectMatchSource(mode));
            searchResult.Elapsed = sw.Elapsed;
            return searchResult;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new SearchResult { TimedOut = true, Elapsed = sw.Elapsed };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Search failed for mode {Mode} with query '{Query}'", mode, normalizedQuery);
            sw.Stop();
            return new SearchResult
            {
                HasError = true,
                ErrorMessage = "The search could not be completed due to a storage error.",
                Elapsed = sw.Elapsed
            };
        }
    }

    /// <summary>
    /// Gets available filter options (queues, servers, recurring job IDs, tags).
    /// Called when the filter panel is opened.
    /// </summary>
    public FilterOptions GetFilterOptions()
    {
        var options = new FilterOptions();
        var monitoringApi = _storage.GetMonitoringApi();

        // Queues
        var queues = monitoringApi.Queues();
        options.Queues = queues
            .Select(q => q.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Servers
        var servers = monitoringApi.Servers();
        options.Servers = servers
            .Select(s => s.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Recurring job IDs
        using (var connection = _storage.GetReadOnlyConnection())
        {
            if (connection is JobStorageConnection storageConnection)
            {
                var recurringJobs = storageConnection.GetRecurringJobs();
                options.RecurringJobIds = recurringJobs
                    .Select(r => r.Id)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        // Tags
        try
        {
            var tags = _tagsReader.GetAllTags();
            options.Tags = tags
                .Where(t => !string.IsNullOrEmpty(t))
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();
            options.TagsFeatureAvailable = true;
        }
        catch
        {
            options.TagsFeatureAvailable = false;
            options.Tags = new List<string>();
        }

        return options;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Private: Build unified criteria from SearchRequest
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Converts a SearchRequest + detected mode into a unified JobFilterCriteria.
    /// </summary>
    private static JobFilterCriteria BuildCriteria(SearchRequest request, SearchMode mode, string normalizedQuery)
    {
        var criteria = new JobFilterCriteria
        {
            // From advanced filters
            States = request.States?.Count > 0 ? request.States : null,
            DateFrom = request.DateFrom.HasValue ? new DateTimeOffset(request.DateFrom.Value) : null,
            DateTo = request.DateTo.HasValue ? new DateTimeOffset(request.DateTo.Value) : null,
            Queue = request.Queue,
            Server = request.Server,
            MinDuration = request.MinDurationSeconds.HasValue
                ? TimeSpan.FromSeconds(request.MinDurationSeconds.Value) : null,
            MaxDuration = request.MaxDurationSeconds.HasValue
                ? TimeSpan.FromSeconds(request.MaxDurationSeconds.Value) : null,
            Tags = request.Tags?.Count > 0 ? request.Tags : null,
            RecurringJobId = request.RecurringJobId,

            // Content search
            ContentPattern = request.ContentQuery,
            SearchStackTrace = request.SearchStackTrace,
            SearchConsoleOutput = request.SearchConsoleOutput,
        };

        // From search mode
        switch (mode)
        {
            case SearchMode.Name:
                criteria.JobNamePattern = normalizedQuery;
                break;
            case SearchMode.Exception:
                criteria.ExceptionPattern = normalizedQuery;
                break;
            case SearchMode.Queue when string.IsNullOrEmpty(criteria.Queue):
                // "queue:xxx" prefix sets the queue filter
                criteria.Queue = normalizedQuery;
                break;
            case SearchMode.Tag:
                // "tag:xxx" prefix sets the tags filter
                criteria.Tags = new List<string> { normalizedQuery };
                break;
        }

        return criteria;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Private: ID lookup (direct, no filter)
    // ═══════════════════════════════════════════════════════════════════════════

    private SearchResult SearchByIdDirect(string jobId)
    {
        var result = new SearchResult();

        try
        {
            using var connection = _storage.GetReadOnlyConnection();
            var jobData = connection.GetJobData(jobId);

            if (jobData == null)
                return result;

            var item = new SearchResultItem
            {
                JobId = jobId,
                State = jobData.State ?? "Unknown",
                CreatedAt = jobData.CreatedAt,
                MatchSource = SearchMatchSource.Id
            };

            if (jobData.Job != null)
            {
                item.JobName = $"{jobData.Job.Type.Name}.{jobData.Job.Method.Name}";
                item.Queue = jobData.Job.Queue;
            }
            else
            {
                item.JobName = "Unknown";
            }

            // Try to get last state change from monitoring API
            try
            {
                var monitoringApi = _storage.GetMonitoringApi();
                var details = monitoringApi.JobDetails(jobId);
                if (details?.History != null && details.History.Count > 0)
                {
                    item.LastStateChange = details.History[0].CreatedAt;
                }
            }
            catch { /* acceptable — we still have basic info */ }

            result.Items.Add(item);
            result.TotalCount = 1;
        }
        catch (Exception)
        {
            result.HasError = true;
            result.ErrorMessage = "The search could not be completed due to a storage error.";
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Private: Helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private static SearchResult ConvertPagedResult(PagedResult<JobSummaryDto> pagedResult, SearchMatchSource matchSource)
    {
        var result = new SearchResult
        {
            TotalCount = (int)Math.Min(pagedResult.TotalCount, int.MaxValue)
        };

        foreach (var dto in pagedResult.Items)
        {
            result.Items.Add(new SearchResultItem
            {
                JobId = dto.JobId,
                JobName = dto.JobName ?? "Unknown",
                State = dto.State ?? "Unknown",
                Queue = dto.Queue,
                CreatedAt = dto.CreatedAt,
                LastStateChange = dto.LastStateChange,
                DurationMs = dto.DurationMs,
                Tags = dto.Tags,
                ExceptionExcerpt = dto.ExceptionMessage,
                MatchSource = matchSource
            });
        }

        return result;
    }

    private static SearchMatchSource DetectMatchSource(SearchMode mode)
    {
        return mode switch
        {
            SearchMode.Name => SearchMatchSource.Name,
            SearchMode.Exception => SearchMatchSource.Exception,
            SearchMode.Queue => SearchMatchSource.Queue,
            SearchMode.Tag => SearchMatchSource.Tag,
            _ => SearchMatchSource.Name
        };
    }

    /// <summary>
    /// Detects the search mode from the raw query string.
    /// </summary>
    internal static (SearchMode Mode, string NormalizedQuery) DetectSearchMode(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return (SearchMode.Auto, "");

        var trimmed = query.Trim();
        if (trimmed.Length > MaxQueryLength)
            trimmed = trimmed[..MaxQueryLength];

        // Check for "queue:" prefix
        if (trimmed.StartsWith("queue:", StringComparison.OrdinalIgnoreCase))
        {
            var value = trimmed["queue:".Length..].Trim();
            return string.IsNullOrWhiteSpace(value) ? (SearchMode.Auto, "") : (SearchMode.Queue, value);
        }

        // Check for "tag:" prefix
        if (trimmed.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
        {
            var value = trimmed["tag:".Length..].Trim();
            return string.IsNullOrWhiteSpace(value) ? (SearchMode.Auto, "") : (SearchMode.Tag, value);
        }

        // Check for "exception:" prefix
        if (trimmed.StartsWith("exception:", StringComparison.OrdinalIgnoreCase))
        {
            var value = trimmed["exception:".Length..].Trim();
            return string.IsNullOrWhiteSpace(value) ? (SearchMode.Auto, "") : (SearchMode.Exception, value);
        }

        // All-digits → ID mode
        if (trimmed.Length >= 1 && trimmed.Length <= MaxIdLength && trimmed.All(char.IsDigit))
            return (SearchMode.Id, trimmed);

        // Default: Name mode (requires minimum 2 characters)
        if (trimmed.Length < MinNameLength)
            return (SearchMode.Auto, trimmed);

        return (SearchMode.Name, trimmed);
    }
}
