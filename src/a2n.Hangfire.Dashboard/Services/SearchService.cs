using System.Diagnostics;
using Hangfire;
using Hangfire.Common;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using Microsoft.Extensions.Logging;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Orchestrates job search queries across Hangfire storage.
/// Supports search by ID, name, queue, tag, and exception text,
/// with advanced filtering and pagination.
/// When a dedicated IStorageQueryProvider (non-GenericQueryProvider) is available,
/// delegates to it for database-level optimized queries.
/// </summary>
public class SearchService
{
    private const int MaxQueryLength = 200;
    private const int MinNameLength = 2;
    private const int MaxIdLength = 20;
    private const int BatchSize = 100;
    private const int SafetyCap = 1000;

    private static readonly string[] AllStates = { "Enqueued", "Processing", "Scheduled", "Succeeded", "Failed", "Deleted" };

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
        // GenericQueryProvider does the same scan as SearchService, so no benefit in delegating to it
        _hasDedicatedProvider = queryProvider != null && queryProvider is not GenericQueryProvider;
        _logger = logger;
    }

    /// <summary>
    /// Executes a search with the given request parameters.
    /// Returns paginated results matching all criteria (AND logic between filters).
    /// When a dedicated storage query provider is available, delegates to it for
    /// database-level optimized queries (Name, Tag, Exception modes).
    /// </summary>
    public Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken ct)
    {
        var (mode, normalizedQuery) = DetectSearchMode(request.Query);

        // If mode was explicitly set on the request, use that instead of auto-detection
        if (request.Mode != SearchMode.Auto)
        {
            mode = request.Mode;
            normalizedQuery = request.Query?.Trim() ?? "";
        }

        // Filter-only search: no text query but has active filters
        if (mode == SearchMode.Auto && string.IsNullOrEmpty(normalizedQuery) && HasActiveSecondaryFilters(request))
        {
            return SearchByFiltersOnlyAsync(request, ct);
        }

        // Delegate to dedicated provider for supported modes
        if (_hasDedicatedProvider && mode is SearchMode.Name or SearchMode.Tag or SearchMode.Exception)
        {
            return SearchViaDedicatedProviderAsync(mode, normalizedQuery, request, ct);
        }

        return mode switch
        {
            SearchMode.Id => SearchByIdAsync(normalizedQuery, request, ct),
            SearchMode.Name => SearchByNameAsync(normalizedQuery, request, ct),
            SearchMode.Queue => SearchByQueueAsync(normalizedQuery, request, ct),
            SearchMode.Tag => SearchByTagAsync(normalizedQuery, request, ct),
            SearchMode.Exception => SearchByExceptionAsync(normalizedQuery, request, ct),
            _ => Task.FromResult(new SearchResult())
        };
    }

    /// <summary>
    /// Delegates search to the dedicated IStorageQueryProvider for database-level queries.
    /// Converts PagedResult to SearchResult for compatibility with the existing UI.
    /// Falls back to scan-based search if the dedicated provider throws an exception.
    /// </summary>
    private async Task<SearchResult> SearchViaDedicatedProviderAsync(
        SearchMode mode, string query, SearchRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new SearchResult();

        try
        {
            var page = (request.From / Math.Max(request.PageSize, 1)) + 1;
            var pageSize = Math.Min(request.PageSize, 50);

            switch (mode)
            {
                case SearchMode.Name:
                    var nameResult = await _queryProvider.SearchJobsByNameAsync(query, page, pageSize, ct);
                    result = ConvertPagedResult(nameResult, SearchMatchSource.Name);
                    break;

                case SearchMode.Tag:
                    var tagResult = await _queryProvider.GetJobsByTagAsync(query, page, pageSize, ct);
                    result = ConvertPagedResult(tagResult, SearchMatchSource.Tag);
                    break;

                case SearchMode.Exception:
                    var exResult = await _queryProvider.SearchFailedByExceptionAsync(query, page, pageSize, ct);
                    result = ConvertPagedResult(exResult, SearchMatchSource.Exception);
                    break;
            }

            // Apply secondary filters (date, state, duration, server, tags, queue, recurring job ID)
            // that are not handled by the dedicated provider's query
            if (result.Items.Count > 0 && HasActiveSecondaryFilters(request))
            {
                result.Items = ApplySecondaryFilters(result.Items, request, ct);
                result.TotalCount = result.Items.Count;
            }
        }
        catch (OperationCanceledException)
        {
            result.TimedOut = true;
        }
        catch (Exception ex)
        {
            // Log the actual exception for debugging
            _logger?.LogError(ex, "Dedicated storage provider failed for search mode {Mode} with query '{Query}'. Falling back to scan-based search.", mode, query);

            // Fallback to scan-based search instead of showing error
            try
            {
                sw.Stop();
                var fallbackResult = mode switch
                {
                    SearchMode.Name => await SearchByNameAsync(query, request, ct),
                    SearchMode.Tag => await SearchByTagAsync(query, request, ct),
                    SearchMode.Exception => await SearchByExceptionAsync(query, request, ct),
                    _ => result
                };
                return fallbackResult;
            }
            catch (Exception fallbackEx)
            {
                _logger?.LogError(fallbackEx, "Fallback scan-based search also failed for mode {Mode}", mode);
                result.HasError = true;
                result.ErrorMessage = "The search could not be completed due to a storage error.";
            }
        }

        sw.Stop();
        result.Elapsed = sw.Elapsed;
        return result;
    }

    /// <summary>
    /// Handles filter-only search (no text query, but one or more filters are active).
    /// Delegates to IStorageQueryProvider.GetJobsWithFilterAsync if a dedicated provider is available,
    /// otherwise scans all states and applies secondary filters.
    /// </summary>
    private async Task<SearchResult> SearchByFiltersOnlyAsync(SearchRequest request, CancellationToken ct)
    {
        // Try dedicated provider first (database-level filtering)
        if (_hasDedicatedProvider)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var page = (request.From / Math.Max(request.PageSize, 1)) + 1;
                var pageSize = Math.Min(request.PageSize, 50);

                var criteria = new JobFilterCriteria
                {
                    State = request.States?.Count == 1 ? request.States[0] : null,
                    DateFrom = request.DateFrom.HasValue ? new DateTimeOffset(request.DateFrom.Value) : null,
                    DateTo = request.DateTo.HasValue ? new DateTimeOffset(request.DateTo.Value) : null,
                    Queue = request.Queue,
                    Server = request.Server,
                    MinDuration = request.MinDurationSeconds.HasValue
                        ? TimeSpan.FromSeconds(request.MinDurationSeconds.Value) : null,
                    MaxDuration = request.MaxDurationSeconds.HasValue
                        ? TimeSpan.FromSeconds(request.MaxDurationSeconds.Value) : null,
                    Tags = request.Tags,
                    RecurringJobId = request.RecurringJobId
                };

                var pagedResult = await _queryProvider.GetJobsWithFilterAsync(criteria, page, pageSize, ct);
                var result = ConvertPagedResult(pagedResult, SearchMatchSource.Name);

                // Apply state filter for multi-state case (provider only supports single state)
                if (request.States?.Count > 1 && result.Items.Count > 0)
                {
                    var stateSet = new HashSet<string>(request.States, StringComparer.OrdinalIgnoreCase);
                    result.Items = result.Items
                        .Where(i => !string.IsNullOrEmpty(i.State) && stateSet.Contains(i.State))
                        .ToList();
                    result.TotalCount = result.Items.Count;
                }

                sw.Stop();
                result.Elapsed = sw.Elapsed;
                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Dedicated provider GetJobsWithFilterAsync failed, falling back to scan-based filter search");
            }
        }

        // Fallback: scan all states and apply filters
        var fallbackSw = Stopwatch.StartNew();
        var fallbackResult = new SearchResult();

        try
        {
            var monitoringApi = _storage.GetMonitoringApi();
            var candidates = new List<SearchResultItem>();

            var statesToScan = request.States != null && request.States.Count > 0
                ? request.States
                : new List<string>(AllStates);

            foreach (var state in statesToScan)
            {
                if (ct.IsCancellationRequested || candidates.Count >= SafetyCap)
                    break;

                ScanStateForNameMatch(monitoringApi, state, null, candidates, ct);
            }

            candidates = ApplySecondaryFilters(candidates, request, ct);
            SortAndPaginate(candidates, fallbackResult, request.From, request.PageSize);
        }
        catch (OperationCanceledException)
        {
            fallbackResult.TimedOut = true;
        }
        catch (Exception)
        {
            fallbackResult.HasError = true;
            fallbackResult.ErrorMessage = "The search could not be completed due to a storage error.";
        }

        fallbackSw.Stop();
        fallbackResult.Elapsed = fallbackSw.Elapsed;
        return fallbackResult;
    }

    /// <summary>
    /// Converts a PagedResult&lt;JobSummaryDto&gt; from IStorageQueryProvider to a SearchResult.
    /// </summary>
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

    /// <summary>
    /// Performs a direct job lookup by ID using JobStorageConnection.GetJobData().
    /// Returns a single-item result if the job exists, or an empty result if not found.
    /// Catches storage errors and returns a result with HasError flag set.
    /// </summary>
    private Task<SearchResult> SearchByIdAsync(string jobId, SearchRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new SearchResult();

        try
        {
            ct.ThrowIfCancellationRequested();

            using var connection = _storage.GetReadOnlyConnection();
            var jobData = connection.GetJobData(jobId);

            if (jobData == null)
            {
                // Job does not exist — return empty result
                sw.Stop();
                result.Elapsed = sw.Elapsed;
                return Task.FromResult(result);
            }

            // Build the result item from JobData
            var item = new SearchResultItem
            {
                JobId = jobId,
                State = jobData.State ?? "Unknown",
                CreatedAt = jobData.CreatedAt,
                MatchSource = SearchMatchSource.Id
            };

            // Extract job name from Job.Type and Job.Method
            if (jobData.Job != null)
            {
                item.JobName = $"{jobData.Job.Type.Name}.{jobData.Job.Method.Name}";
                item.Queue = jobData.Job.Queue;
            }
            else
            {
                item.JobName = "Unknown";
            }

            // Try to get additional details (state history) from MonitoringApi for timestamps
            try
            {
                var monitoringApi = _storage.GetMonitoringApi();
                var details = monitoringApi.JobDetails(jobId);

                if (details?.History != null && details.History.Count > 0)
                {
                    // Most recent state entry is the last state change
                    item.LastStateChange = details.History[0].CreatedAt;

                    // Find the "Created" state for CreatedAt (fallback to JobData.CreatedAt)
                    var createdState = details.History
                        .FirstOrDefault(h => string.Equals(h.StateName, "Created", StringComparison.OrdinalIgnoreCase));
                    if (createdState != null)
                    {
                        item.CreatedAt = createdState.CreatedAt;
                    }

                    // Try to extract queue from Enqueued state data
                    if (string.IsNullOrEmpty(item.Queue))
                    {
                        var enqueuedState = details.History
                            .FirstOrDefault(h => string.Equals(h.StateName, "Enqueued", StringComparison.OrdinalIgnoreCase));
                        if (enqueuedState?.Data != null && enqueuedState.Data.TryGetValue("Queue", out var queue))
                        {
                            item.Queue = queue;
                        }
                    }
                }
            }
            catch
            {
                // If we can't get details, we still have the basic info from GetJobData
                // LastStateChange will remain null, which is acceptable
            }

            result.Items.Add(item);

            // Apply secondary filters to the single result
            result.Items = ApplySecondaryFilters(result.Items, request, ct);
            result.TotalCount = result.Items.Count;
        }
        catch (OperationCanceledException)
        {
            result.TimedOut = true;
        }
        catch (Exception)
        {
            result.HasError = true;
            result.ErrorMessage = "The search could not be completed due to a storage error.";
        }

        sw.Stop();
        result.Elapsed = sw.Elapsed;
        return Task.FromResult(result);
    }

    /// <summary>
    /// Sorts candidates by CreatedAt descending and applies pagination.
    /// Enforces a maximum page size of 50 items.
    /// Sets TotalCount on the result to the total number of candidates before pagination.
    /// </summary>
    internal static void SortAndPaginate(List<SearchResultItem> candidates, SearchResult result, int from, int pageSize)
    {
        // Sort by CreatedAt descending (most recent first)
        candidates.Sort((a, b) =>
        {
            var aTime = a.CreatedAt ?? DateTime.MinValue;
            var bTime = b.CreatedAt ?? DateTime.MinValue;
            return bTime.CompareTo(aTime);
        });

        // Apply pagination with max 50 items per page
        result.TotalCount = candidates.Count;
        result.Items = candidates
            .Skip(from)
            .Take(Math.Min(pageSize, 50))
            .ToList();
    }

    /// <summary>
    /// Searches for jobs by type name or method name using a scan-and-filter approach.
    /// Scans jobs across states in batches, filters by case-insensitive substring match,
    /// applies a safety cap of 1000 candidates, sorts by creation time descending,
    /// and returns a paginated result.
    /// </summary>
    private Task<SearchResult> SearchByNameAsync(string query, SearchRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new SearchResult();

        try
        {
            ct.ThrowIfCancellationRequested();

            var monitoringApi = _storage.GetMonitoringApi();
            var candidates = new List<SearchResultItem>();

            // Determine which states to scan
            var statesToScan = request.States != null && request.States.Count > 0
                ? request.States
                : new List<string>(AllStates);

            foreach (var state in statesToScan)
            {
                if (ct.IsCancellationRequested)
                    break;

                if (candidates.Count >= SafetyCap)
                    break;

                ScanStateForNameMatch(monitoringApi, state, query, candidates, ct);
            }

            // Apply secondary filters (date, duration, server, tags, queue, recurring job ID)
            // Note: state filter is already applied during scanning for Name search
            candidates = ApplySecondaryFilters(candidates, request, ct);

            // Sort and paginate
            SortAndPaginate(candidates, result, request.From, request.PageSize);
        }
        catch (OperationCanceledException)
        {
            result.TimedOut = true;
        }
        catch (Exception)
        {
            result.HasError = true;
            result.ErrorMessage = "The search could not be completed due to a storage error.";
        }

        sw.Stop();
        result.Elapsed = sw.Elapsed;
        return Task.FromResult(result);
    }

    /// <summary>
    /// Searches for jobs by tag using TagsDataReader for set-based lookup.
    /// Performs a case-insensitive exact match against job tags.
    /// Returns results ordered by creation time descending, paginated.
    /// </summary>
    private Task<SearchResult> SearchByTagAsync(string tagName, SearchRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new SearchResult();

        try
        {
            ct.ThrowIfCancellationRequested();

            // Find the actual tag name (case-insensitive match) from all known tags
            var allTags = _tagsReader.GetAllTags();
            var matchedTag = allTags.FirstOrDefault(t =>
                string.Equals(t, tagName, StringComparison.OrdinalIgnoreCase));

            // If no matching tag found, return empty result
            if (matchedTag == null)
            {
                sw.Stop();
                result.Elapsed = sw.Elapsed;
                return Task.FromResult(result);
            }

            ct.ThrowIfCancellationRequested();

            // Get total count of jobs with this tag
            var totalJobCount = _tagsReader.GetJobCountByTag(matchedTag);

            if (totalJobCount == 0)
            {
                sw.Stop();
                result.Elapsed = sw.Elapsed;
                return Task.FromResult(result);
            }

            // Retrieve all job IDs for this tag (up to safety cap) to sort by CreatedAt
            var jobIdsToFetch = (int)Math.Min(totalJobCount, SafetyCap);
            var jobIds = _tagsReader.GetJobsByTag(matchedTag, 0, jobIdsToFetch);

            ct.ThrowIfCancellationRequested();

            // Build SearchResultItems for each job ID
            var candidates = new List<SearchResultItem>();
            using var connection = _storage.GetReadOnlyConnection();

            foreach (var jobId in jobIds)
            {
                if (ct.IsCancellationRequested)
                    break;

                var jobData = connection.GetJobData(jobId);
                if (jobData == null)
                    continue;

                var item = new SearchResultItem
                {
                    JobId = jobId,
                    State = jobData.State ?? "Unknown",
                    CreatedAt = jobData.CreatedAt,
                    MatchSource = SearchMatchSource.Tag
                };

                // Extract job name from Job.Type and Job.Method
                if (jobData.Job != null)
                {
                    item.JobName = $"{jobData.Job.Type.Name}.{jobData.Job.Method.Name}";
                    item.Queue = jobData.Job.Queue;
                }
                else
                {
                    item.JobName = "Unknown";
                }

                // Get tags for this job
                try
                {
                    item.Tags = _tagsReader.GetJobTags(jobId);
                }
                catch
                {
                    // If we can't get tags, leave as null
                }

                candidates.Add(item);
            }

            // Apply secondary filters
            candidates = ApplySecondaryFilters(candidates, request, ct);

            // Sort and paginate
            SortAndPaginate(candidates, result, request.From, request.PageSize);
        }
        catch (OperationCanceledException)
        {
            result.TimedOut = true;
        }
        catch (Exception)
        {
            result.HasError = true;
            result.ErrorMessage = "The search could not be completed due to a storage error.";
        }

        sw.Stop();
        result.Elapsed = sw.Elapsed;
        return Task.FromResult(result);
    }

    /// <summary>
    /// Searches for jobs by queue name using case-insensitive exact match.
    /// Gets all queues from IMonitoringApi, finds the matching queue,
    /// then scans all enqueued jobs in that queue in batches.
    /// Collects up to 1000 results, sorts by CreatedAt descending, and applies pagination.
    /// </summary>
    private Task<SearchResult> SearchByQueueAsync(string queueName, SearchRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new SearchResult();

        try
        {
            ct.ThrowIfCancellationRequested();

            var monitoringApi = _storage.GetMonitoringApi();

            // Get all queues and find the one matching case-insensitively
            var queues = monitoringApi.Queues();
            var matchedQueue = queues.FirstOrDefault(q =>
                string.Equals(q.Name, queueName, StringComparison.OrdinalIgnoreCase));

            if (matchedQueue == null)
            {
                // No matching queue found — return empty result
                sw.Stop();
                result.Elapsed = sw.Elapsed;
                return Task.FromResult(result);
            }

            var candidates = new List<SearchResultItem>();
            int from = 0;

            while (!ct.IsCancellationRequested && candidates.Count < SafetyCap)
            {
                var batch = monitoringApi.EnqueuedJobs(matchedQueue.Name, from, BatchSize);
                if (batch == null || batch.Count == 0)
                    break;

                foreach (var entry in batch)
                {
                    if (candidates.Count >= SafetyCap)
                        break;

                    var dto = entry.Value;
                    string jobName = "Unknown";

                    if (dto?.Job != null)
                    {
                        jobName = $"{dto.Job.Type.Name}.{dto.Job.Method.Name}";
                    }

                    candidates.Add(new SearchResultItem
                    {
                        JobId = entry.Key,
                        JobName = jobName,
                        State = "Enqueued",
                        Queue = matchedQueue.Name,
                        CreatedAt = dto?.EnqueuedAt,
                        LastStateChange = dto?.EnqueuedAt,
                        MatchSource = SearchMatchSource.Queue
                    });
                }

                from += BatchSize;
            }

            // Apply secondary filters
            candidates = ApplySecondaryFilters(candidates, request, ct);

            // Sort and paginate
            SortAndPaginate(candidates, result, request.From, request.PageSize);
        }
        catch (OperationCanceledException)
        {
            result.TimedOut = true;
        }
        catch (Exception)
        {
            result.HasError = true;
            result.ErrorMessage = "The search could not be completed due to a storage error.";
        }

        sw.Stop();
        result.Elapsed = sw.Elapsed;
        return Task.FromResult(result);
    }

    /// <summary>
    /// Searches for failed jobs whose ExceptionMessage or ExceptionDetails contain the query
    /// as a case-insensitive substring. Generates a truncated excerpt (max 200 chars) centered
    /// around the match position with the search term included.
    /// </summary>
    private Task<SearchResult> SearchByExceptionAsync(string exceptionQuery, SearchRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new SearchResult();

        // Validate non-empty query after prefix
        if (string.IsNullOrWhiteSpace(exceptionQuery))
        {
            sw.Stop();
            result.Elapsed = sw.Elapsed;
            result.HasError = true;
            result.ErrorMessage = "A search term is required for exception search.";
            return Task.FromResult(result);
        }

        try
        {
            ct.ThrowIfCancellationRequested();

            var monitoringApi = _storage.GetMonitoringApi();
            var candidates = new List<SearchResultItem>();

            int from = 0;
            while (!ct.IsCancellationRequested && candidates.Count < SafetyCap)
            {
                var batch = monitoringApi.FailedJobs(from, BatchSize);
                if (batch == null || batch.Count == 0)
                    break;

                foreach (var entry in batch)
                {
                    if (candidates.Count >= SafetyCap)
                        break;

                    var dto = entry.Value;
                    if (dto == null)
                        continue;

                    // Check ExceptionMessage and ExceptionDetails for match
                    string matchedText = null;
                    if (!string.IsNullOrEmpty(dto.ExceptionMessage) &&
                        dto.ExceptionMessage.Contains(exceptionQuery, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedText = dto.ExceptionMessage;
                    }
                    else if (!string.IsNullOrEmpty(dto.ExceptionDetails) &&
                             dto.ExceptionDetails.Contains(exceptionQuery, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedText = dto.ExceptionDetails;
                    }

                    if (matchedText != null)
                    {
                        var item = new SearchResultItem
                        {
                            JobId = entry.Key,
                            State = "Failed",
                            CreatedAt = dto.FailedAt,
                            LastStateChange = dto.FailedAt,
                            MatchSource = SearchMatchSource.Exception,
                            ExceptionExcerpt = GenerateExcerpt(matchedText, exceptionQuery, 200)
                        };

                        // Extract job name from Job
                        if (dto.Job != null)
                        {
                            item.JobName = $"{dto.Job.Type.Name}.{dto.Job.Method.Name}";
                            item.Queue = dto.Job.Queue;
                        }
                        else
                        {
                            item.JobName = "Unknown";
                        }

                        candidates.Add(item);
                    }
                }

                from += BatchSize;
            }

            // Apply secondary filters
            candidates = ApplySecondaryFilters(candidates, request, ct);

            // Sort and paginate
            SortAndPaginate(candidates, result, request.From, request.PageSize);
        }
        catch (OperationCanceledException)
        {
            result.TimedOut = true;
        }
        catch (Exception)
        {
            result.HasError = true;
            result.ErrorMessage = "The search could not be completed due to a storage error.";
        }

        sw.Stop();
        result.Elapsed = sw.Elapsed;
        return Task.FromResult(result);
    }

    /// <summary>
    /// Checks whether any secondary filter is active on the request.
    /// Used to skip expensive post-filtering when no filters are set.
    /// </summary>
    private static bool HasActiveSecondaryFilters(SearchRequest request)
    {
        return request.DateFrom.HasValue
            || request.DateTo.HasValue
            || (request.States != null && request.States.Count > 0)
            || !string.IsNullOrEmpty(request.Server)
            || request.MinDurationSeconds.HasValue
            || request.MaxDurationSeconds.HasValue
            || (request.Tags != null && request.Tags.Count > 0)
            || !string.IsNullOrEmpty(request.Queue)
            || !string.IsNullOrEmpty(request.RecurringJobId);
    }

    /// <summary>
    /// Applies secondary filters (AND logic between different filter types) to a list of candidates.
    /// Filters applied: date range, state, server, duration, tags, queue, recurring job ID.
    /// Only performs expensive lookups (server, tags, recurring job ID) when the respective filter is active.
    /// </summary>
    internal List<SearchResultItem> ApplySecondaryFilters(List<SearchResultItem> candidates, SearchRequest request, CancellationToken ct)
    {
        if (candidates.Count == 0)
            return candidates;

        var filtered = candidates;

        // Date range filter: filter by CreatedAt within [From, To] inclusive
        if (request.DateFrom.HasValue)
        {
            filtered = filtered.Where(item => item.CreatedAt.HasValue && item.CreatedAt.Value >= request.DateFrom.Value).ToList();
        }
        if (request.DateTo.HasValue)
        {
            filtered = filtered.Where(item => item.CreatedAt.HasValue && item.CreatedAt.Value <= request.DateTo.Value).ToList();
        }

        // State filter: filter by current state membership in selected set
        if (request.States != null && request.States.Count > 0)
        {
            var stateSet = new HashSet<string>(request.States, StringComparer.OrdinalIgnoreCase);
            filtered = filtered.Where(item => !string.IsNullOrEmpty(item.State) && stateSet.Contains(item.State)).ToList();
        }

        // Server filter: filter by Processing state ServerId match
        if (!string.IsNullOrEmpty(request.Server))
        {
            ct.ThrowIfCancellationRequested();
            var monitoringApi = _storage.GetMonitoringApi();
            filtered = filtered.Where(item =>
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var details = monitoringApi.JobDetails(item.JobId);
                    if (details?.History == null)
                        return false;

                    return details.History.Any(h =>
                        string.Equals(h.StateName, "Processing", StringComparison.OrdinalIgnoreCase) &&
                        h.Data != null &&
                        h.Data.TryGetValue("ServerId", out var serverId) &&
                        string.Equals(serverId, request.Server, StringComparison.OrdinalIgnoreCase));
                }
                catch
                {
                    return false;
                }
            }).ToList();
        }

        // Duration filter: filter by execution duration within [min, max] seconds inclusive
        if (request.MinDurationSeconds.HasValue || request.MaxDurationSeconds.HasValue)
        {
            double? minMs = request.MinDurationSeconds.HasValue ? request.MinDurationSeconds.Value * 1000.0 : null;
            double? maxMs = request.MaxDurationSeconds.HasValue ? request.MaxDurationSeconds.Value * 1000.0 : null;

            filtered = filtered.Where(item =>
            {
                // If DurationMs is null (job hasn't completed), exclude it when duration filter is active
                if (!item.DurationMs.HasValue)
                    return false;

                if (minMs.HasValue && item.DurationMs.Value < minMs.Value)
                    return false;

                if (maxMs.HasValue && item.DurationMs.Value > maxMs.Value)
                    return false;

                return true;
            }).ToList();
        }

        // Tags filter: OR logic among selected tags, AND with other filters
        if (request.Tags != null && request.Tags.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var tagSet = new HashSet<string>(request.Tags, StringComparer.OrdinalIgnoreCase);

            filtered = filtered.Where(item =>
            {
                ct.ThrowIfCancellationRequested();
                // If tags are already populated on the item, use them
                var jobTags = item.Tags;
                if (jobTags == null)
                {
                    // Look up tags via TagsDataReader
                    try
                    {
                        jobTags = _tagsReader.GetJobTags(item.JobId);
                        item.Tags = jobTags; // Cache for later use
                    }
                    catch
                    {
                        return false;
                    }
                }

                // OR logic: job must have at least one of the selected tags
                return jobTags.Any(t => tagSet.Contains(t));
            }).ToList();
        }

        // Queue dropdown filter: case-insensitive queue match
        if (!string.IsNullOrEmpty(request.Queue))
        {
            filtered = filtered.Where(item =>
                !string.IsNullOrEmpty(item.Queue) &&
                string.Equals(item.Queue, request.Queue, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }

        // Recurring Job ID filter: match RecurringJobId job parameter
        if (!string.IsNullOrEmpty(request.RecurringJobId))
        {
            ct.ThrowIfCancellationRequested();
            using var connection = _storage.GetReadOnlyConnection();

            filtered = filtered.Where(item =>
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (connection is JobStorageConnection storageConnection)
                    {
                        var recurringJobId = storageConnection.GetJobParameter(item.JobId, "RecurringJobId");
                        return string.Equals(recurringJobId, request.RecurringJobId, StringComparison.OrdinalIgnoreCase);
                    }
                    return false;
                }
                catch
                {
                    return false;
                }
            }).ToList();
        }

        return filtered;
    }

    /// <summary>
    /// Generates a truncated excerpt (max maxLength chars) from the source text,
    /// centered around the first occurrence of the search term.
    /// The excerpt always contains the search term as a substring.
    /// </summary>
    internal static string GenerateExcerpt(string text, string searchTerm, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        // If the text fits within maxLength, return it as-is
        if (text.Length <= maxLength)
            return text;

        // Find the position of the search term (case-insensitive)
        int matchIndex = text.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase);
        if (matchIndex < 0)
            return text.Substring(0, maxLength);

        // Calculate window centered around the match
        int matchEnd = matchIndex + searchTerm.Length;
        int halfWindow = (maxLength - searchTerm.Length) / 2;

        int start = matchIndex - halfWindow;
        int end = start + maxLength;

        // Adjust if start is before the beginning
        if (start < 0)
        {
            start = 0;
            end = maxLength;
        }

        // Adjust if end is past the text length
        if (end > text.Length)
        {
            end = text.Length;
            start = Math.Max(0, end - maxLength);
        }

        return text.Substring(start, end - start);
    }

    /// <summary>
    /// Scans a specific state for jobs matching the name query.
    /// For Enqueued state, iterates over all queues.
    /// </summary>
    private void ScanStateForNameMatch(
        IMonitoringApi monitoringApi,
        string state,
        string query,
        List<SearchResultItem> candidates,
        CancellationToken ct)
    {
        switch (state.ToLowerInvariant())
        {
            case "enqueued":
                ScanEnqueuedJobs(monitoringApi, query, candidates, ct);
                break;
            case "processing":
                ScanProcessingJobs(monitoringApi, query, candidates, ct);
                break;
            case "scheduled":
                ScanScheduledJobs(monitoringApi, query, candidates, ct);
                break;
            case "succeeded":
                ScanSucceededJobs(monitoringApi, query, candidates, ct);
                break;
            case "failed":
                ScanFailedJobs(monitoringApi, query, candidates, ct);
                break;
            case "deleted":
                ScanDeletedJobs(monitoringApi, query, candidates, ct);
                break;
        }
    }

    private void ScanEnqueuedJobs(
        IMonitoringApi monitoringApi,
        string query,
        List<SearchResultItem> candidates,
        CancellationToken ct)
    {
        // For enqueued jobs, we need to iterate over all queues
        var queues = monitoringApi.Queues();
        foreach (var queue in queues)
        {
            if (ct.IsCancellationRequested || candidates.Count >= SafetyCap)
                break;

            int from = 0;
            while (!ct.IsCancellationRequested && candidates.Count < SafetyCap)
            {
                var batch = monitoringApi.EnqueuedJobs(queue.Name, from, BatchSize);
                if (batch == null || batch.Count == 0)
                    break;

                foreach (var entry in batch)
                {
                    if (candidates.Count >= SafetyCap)
                        break;

                    var dto = entry.Value;
                    if (dto?.Job == null)
                        continue;

                    if (MatchesNameQuery(dto.Job, query))
                    {
                        candidates.Add(new SearchResultItem
                        {
                            JobId = entry.Key,
                            JobName = $"{dto.Job.Type.Name}.{dto.Job.Method.Name}",
                            State = "Enqueued",
                            Queue = queue.Name,
                            CreatedAt = dto.EnqueuedAt,
                            LastStateChange = dto.EnqueuedAt,
                            MatchSource = SearchMatchSource.Name
                        });
                    }
                }

                from += BatchSize;
            }
        }
    }

    private void ScanProcessingJobs(
        IMonitoringApi monitoringApi,
        string query,
        List<SearchResultItem> candidates,
        CancellationToken ct)
    {
        int from = 0;
        while (!ct.IsCancellationRequested && candidates.Count < SafetyCap)
        {
            var batch = monitoringApi.ProcessingJobs(from, BatchSize);
            if (batch == null || batch.Count == 0)
                break;

            foreach (var entry in batch)
            {
                if (candidates.Count >= SafetyCap)
                    break;

                var dto = entry.Value;
                if (dto?.Job == null)
                    continue;

                if (MatchesNameQuery(dto.Job, query))
                {
                    candidates.Add(new SearchResultItem
                    {
                        JobId = entry.Key,
                        JobName = $"{dto.Job.Type.Name}.{dto.Job.Method.Name}",
                        State = "Processing",
                        Queue = dto.Job.Queue,
                        CreatedAt = dto.StartedAt,
                        LastStateChange = dto.StartedAt,
                        MatchSource = SearchMatchSource.Name
                    });
                }
            }

            from += BatchSize;
        }
    }

    private void ScanScheduledJobs(
        IMonitoringApi monitoringApi,
        string query,
        List<SearchResultItem> candidates,
        CancellationToken ct)
    {
        int from = 0;
        while (!ct.IsCancellationRequested && candidates.Count < SafetyCap)
        {
            var batch = monitoringApi.ScheduledJobs(from, BatchSize);
            if (batch == null || batch.Count == 0)
                break;

            foreach (var entry in batch)
            {
                if (candidates.Count >= SafetyCap)
                    break;

                var dto = entry.Value;
                if (dto?.Job == null)
                    continue;

                if (MatchesNameQuery(dto.Job, query))
                {
                    candidates.Add(new SearchResultItem
                    {
                        JobId = entry.Key,
                        JobName = $"{dto.Job.Type.Name}.{dto.Job.Method.Name}",
                        State = "Scheduled",
                        Queue = dto.Job.Queue,
                        CreatedAt = dto.ScheduledAt,
                        LastStateChange = dto.ScheduledAt,
                        MatchSource = SearchMatchSource.Name
                    });
                }
            }

            from += BatchSize;
        }
    }

    private void ScanSucceededJobs(
        IMonitoringApi monitoringApi,
        string query,
        List<SearchResultItem> candidates,
        CancellationToken ct)
    {
        int from = 0;
        while (!ct.IsCancellationRequested && candidates.Count < SafetyCap)
        {
            var batch = monitoringApi.SucceededJobs(from, BatchSize);
            if (batch == null || batch.Count == 0)
                break;

            foreach (var entry in batch)
            {
                if (candidates.Count >= SafetyCap)
                    break;

                var dto = entry.Value;
                if (dto?.Job == null)
                    continue;

                if (MatchesNameQuery(dto.Job, query))
                {
                    candidates.Add(new SearchResultItem
                    {
                        JobId = entry.Key,
                        JobName = $"{dto.Job.Type.Name}.{dto.Job.Method.Name}",
                        State = "Succeeded",
                        Queue = dto.Job.Queue,
                        CreatedAt = dto.SucceededAt,
                        LastStateChange = dto.SucceededAt,
                        DurationMs = dto.TotalDuration,
                        MatchSource = SearchMatchSource.Name
                    });
                }
            }

            from += BatchSize;
        }
    }

    private void ScanFailedJobs(
        IMonitoringApi monitoringApi,
        string query,
        List<SearchResultItem> candidates,
        CancellationToken ct)
    {
        int from = 0;
        while (!ct.IsCancellationRequested && candidates.Count < SafetyCap)
        {
            var batch = monitoringApi.FailedJobs(from, BatchSize);
            if (batch == null || batch.Count == 0)
                break;

            foreach (var entry in batch)
            {
                if (candidates.Count >= SafetyCap)
                    break;

                var dto = entry.Value;
                if (dto?.Job == null)
                    continue;

                if (MatchesNameQuery(dto.Job, query))
                {
                    candidates.Add(new SearchResultItem
                    {
                        JobId = entry.Key,
                        JobName = $"{dto.Job.Type.Name}.{dto.Job.Method.Name}",
                        State = "Failed",
                        Queue = dto.Job.Queue,
                        CreatedAt = dto.FailedAt,
                        LastStateChange = dto.FailedAt,
                        MatchSource = SearchMatchSource.Name
                    });
                }
            }

            from += BatchSize;
        }
    }

    private void ScanDeletedJobs(
        IMonitoringApi monitoringApi,
        string query,
        List<SearchResultItem> candidates,
        CancellationToken ct)
    {
        int from = 0;
        while (!ct.IsCancellationRequested && candidates.Count < SafetyCap)
        {
            var batch = monitoringApi.DeletedJobs(from, BatchSize);
            if (batch == null || batch.Count == 0)
                break;

            foreach (var entry in batch)
            {
                if (candidates.Count >= SafetyCap)
                    break;

                var dto = entry.Value;
                if (dto?.Job == null)
                    continue;

                if (MatchesNameQuery(dto.Job, query))
                {
                    candidates.Add(new SearchResultItem
                    {
                        JobId = entry.Key,
                        JobName = $"{dto.Job.Type.Name}.{dto.Job.Method.Name}",
                        State = "Deleted",
                        Queue = dto.Job.Queue,
                        CreatedAt = dto.DeletedAt,
                        LastStateChange = dto.DeletedAt,
                        MatchSource = SearchMatchSource.Name
                    });
                }
            }

            from += BatchSize;
        }
    }

    /// <summary>
    /// Checks if a job's type name or method name contains the query as a case-insensitive substring.
    /// </summary>
    private static bool MatchesNameQuery(Job job, string query)
    {
        // If query is null or empty, match all jobs (filter-only mode)
        if (string.IsNullOrEmpty(query))
            return true;

        var typeName = job.Type?.Name;
        var methodName = job.Method?.Name;

        if (typeName != null && typeName.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        if (methodName != null && methodName.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Gets available filter options (queues, servers, recurring job IDs, tags).
    /// Called when the filter panel is opened.
    /// </summary>
    public FilterOptions GetFilterOptions()
    {
        var options = new FilterOptions();

        var monitoringApi = _storage.GetMonitoringApi();

        // Retrieve queues
        var queues = monitoringApi.Queues();
        options.Queues = queues
            .Select(q => q.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Retrieve servers
        var servers = monitoringApi.Servers();
        options.Servers = servers
            .Select(s => s.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Retrieve recurring job IDs
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

        // Retrieve tags (conditionally, based on feature availability)
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

    /// <summary>
    /// Detects the search mode from the raw query string.
    /// Also normalizes the query (trims, truncates to 200 chars) and returns
    /// the effective search term (e.g., text after "queue:" prefix).
    /// </summary>
    /// <param name="query">The raw query string from user input.</param>
    /// <returns>
    /// A tuple of (SearchMode, normalizedQuery) where normalizedQuery is the
    /// effective search term to use (prefix stripped for prefixed modes).
    /// Returns (Auto, "") for empty/whitespace queries or invalid inputs.
    /// </returns>
    internal static (SearchMode Mode, string NormalizedQuery) DetectSearchMode(string query)
    {
        // Handle null/empty/whitespace
        if (string.IsNullOrWhiteSpace(query))
        {
            return (SearchMode.Auto, "");
        }

        // Trim whitespace
        var trimmed = query.Trim();

        // Truncate to max 200 characters
        if (trimmed.Length > MaxQueryLength)
        {
            trimmed = trimmed.Substring(0, MaxQueryLength);
        }

        // Check for "queue:" prefix (case-insensitive)
        if (trimmed.StartsWith("queue:", StringComparison.OrdinalIgnoreCase))
        {
            var value = trimmed.Substring("queue:".Length).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return (SearchMode.Auto, "");
            }
            return (SearchMode.Queue, value);
        }

        // Check for "tag:" prefix (case-insensitive)
        if (trimmed.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
        {
            var value = trimmed.Substring("tag:".Length).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return (SearchMode.Auto, "");
            }
            return (SearchMode.Tag, value);
        }

        // Check for "exception:" prefix (case-insensitive)
        if (trimmed.StartsWith("exception:", StringComparison.OrdinalIgnoreCase))
        {
            var value = trimmed.Substring("exception:".Length).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return (SearchMode.Auto, "");
            }
            return (SearchMode.Exception, value);
        }

        // Check for all-digits (1-20 chars) → ID mode
        if (trimmed.Length >= 1 && trimmed.Length <= MaxIdLength && IsAllDigits(trimmed))
        {
            return (SearchMode.Id, trimmed);
        }

        // Default: Name mode (requires minimum 2 characters)
        if (trimmed.Length < MinNameLength)
        {
            return (SearchMode.Auto, trimmed);
        }

        return (SearchMode.Name, trimmed);
    }

    private static bool IsAllDigits(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (!char.IsDigit(value[i]))
            {
                return false;
            }
        }
        return true;
    }
}
