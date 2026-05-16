using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using Hangfire;
using Hangfire.Common;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Fallback IStorageQueryProvider that uses Hangfire's IMonitoringApi.
/// Loads jobs in batches, applies client-side filtering, and paginates in memory.
/// Does NOT implement IStorageMetricsProvider (analytics unavailable without adapter).
/// </summary>
public class GenericQueryProvider : IStorageQueryProvider
{
    private const int BatchSize = 100;
    private const int SafetyCap = 1000;
    private static readonly string[] AllStates =
        { "Enqueued", "Processing", "Scheduled", "Succeeded", "Failed", "Deleted" };

    private readonly JobStorage _storage;
    private readonly TagsDataReader _tagsReader;

    public GenericQueryProvider(JobStorage storage, TagsDataReader tagsReader)
    {
        _storage = storage;
        _tagsReader = tagsReader;
    }

    public Task<PagedResult<JobSummaryDto>> SearchJobsByNameAsync(
        string searchTerm, int page, int pageSize, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
            return Task.FromResult(PagedResult<JobSummaryDto>.Empty(page, pageSize));

        var monitoringApi = _storage.GetMonitoringApi();
        var candidates = new List<JobSummaryDto>();

        foreach (var state in AllStates)
        {
            if (ct.IsCancellationRequested || candidates.Count >= SafetyCap)
                break;

            ScanStateForNameMatch(monitoringApi, state, searchTerm, candidates, ct);
        }

        // Sort by CreatedAt descending and paginate
        return Task.FromResult(SortAndPaginate(candidates, page, pageSize));
    }

    public Task<PagedResult<JobSummaryDto>> SearchFailedByExceptionAsync(
        string searchTerm, int page, int pageSize, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
            return Task.FromResult(PagedResult<JobSummaryDto>.Empty(page, pageSize));

        var monitoringApi = _storage.GetMonitoringApi();
        var candidates = new List<JobSummaryDto>();

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
                bool matches = false;
                string exceptionType = null;
                string exceptionMessage = null;

                if (!string.IsNullOrEmpty(dto.ExceptionMessage) &&
                    dto.ExceptionMessage.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                {
                    matches = true;
                    exceptionMessage = dto.ExceptionMessage;
                    exceptionType = dto.ExceptionType;
                }
                else if (!string.IsNullOrEmpty(dto.ExceptionDetails) &&
                         dto.ExceptionDetails.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                {
                    matches = true;
                    exceptionMessage = dto.ExceptionMessage;
                    exceptionType = dto.ExceptionType;
                }
                else if (!string.IsNullOrEmpty(dto.ExceptionType) &&
                         dto.ExceptionType.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                {
                    matches = true;
                    exceptionMessage = dto.ExceptionMessage;
                    exceptionType = dto.ExceptionType;
                }

                if (matches)
                {
                    string jobName = "Unknown";
                    string queue = null;
                    if (dto.Job != null)
                    {
                        jobName = $"{dto.Job.Type.Name}.{dto.Job.Method.Name}";
                        queue = dto.Job.Queue;
                    }

                    candidates.Add(new JobSummaryDto
                    {
                        JobId = entry.Key,
                        JobName = jobName,
                        State = "Failed",
                        Queue = queue,
                        CreatedAt = dto.FailedAt,
                        LastStateChange = dto.FailedAt,
                        ExceptionType = exceptionType,
                        ExceptionMessage = exceptionMessage
                    });
                }
            }

            from += BatchSize;
        }

        return Task.FromResult(SortAndPaginate(candidates, page, pageSize));
    }

    public Task<PagedResult<JobSummaryDto>> GetJobsWithFilterAsync(
        JobFilterCriteria criteria, int page, int pageSize, CancellationToken ct)
    {
        var monitoringApi = _storage.GetMonitoringApi();
        var candidates = new List<JobSummaryDto>();

        // Determine which states to scan
        var statesToScan = !string.IsNullOrWhiteSpace(criteria?.State)
            ? new[] { criteria.State }
            : AllStates;

        foreach (var state in statesToScan)
        {
            if (ct.IsCancellationRequested || candidates.Count >= SafetyCap)
                break;

            ScanStateForFilter(monitoringApi, state, candidates, ct);
        }

        // Apply client-side filters
        var filtered = ApplyFilterCriteria(candidates, criteria, ct);

        return Task.FromResult(SortAndPaginate(filtered, page, pageSize));
    }

    public Task<PagedResult<JobSummaryDto>> GetJobsByTagAsync(
        string tag, int page, int pageSize, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return Task.FromResult(PagedResult<JobSummaryDto>.Empty(page, pageSize));

        // Find the actual tag name (case-insensitive match)
        var allTags = _tagsReader.GetAllTags();
        var matchedTag = allTags.FirstOrDefault(t =>
            string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));

        if (matchedTag == null)
            return Task.FromResult(PagedResult<JobSummaryDto>.Empty(page, pageSize));

        ct.ThrowIfCancellationRequested();

        var totalJobCount = _tagsReader.GetJobCountByTag(matchedTag);
        if (totalJobCount == 0)
            return Task.FromResult(PagedResult<JobSummaryDto>.Empty(page, pageSize));

        // Retrieve job IDs for this tag (up to safety cap)
        var jobIdsToFetch = (int)Math.Min(totalJobCount, SafetyCap);
        var jobIds = _tagsReader.GetJobsByTag(matchedTag, 0, jobIdsToFetch);

        ct.ThrowIfCancellationRequested();

        var candidates = new List<JobSummaryDto>();
        using var connection = _storage.GetReadOnlyConnection();

        foreach (var jobId in jobIds)
        {
            if (ct.IsCancellationRequested)
                break;

            var jobData = connection.GetJobData(jobId);
            if (jobData == null)
                continue;

            string jobName = "Unknown";
            string queue = null;
            if (jobData.Job != null)
            {
                jobName = $"{jobData.Job.Type.Name}.{jobData.Job.Method.Name}";
                queue = jobData.Job.Queue;
            }

            string[] tags = null;
            try { tags = _tagsReader.GetJobTags(jobId); } catch { }

            candidates.Add(new JobSummaryDto
            {
                JobId = jobId,
                JobName = jobName,
                State = jobData.State ?? "Unknown",
                Queue = queue,
                CreatedAt = jobData.CreatedAt,
                Tags = tags
            });
        }

        return Task.FromResult(SortAndPaginate(candidates, page, pageSize));
    }

    public Task<IReadOnlyList<TagCountDto>> GetTagCloudAsync(CancellationToken ct)
    {
        var allTags = _tagsReader.GetAllTags();
        var result = new List<TagCountDto>();

        foreach (var tag in allTags)
        {
            if (ct.IsCancellationRequested)
                break;

            var count = _tagsReader.GetJobCountByTag(tag);
            result.Add(new TagCountDto { Tag = tag, Count = count });
        }

        // Order by count descending
        result.Sort((a, b) => b.Count.CompareTo(a.Count));

        return Task.FromResult<IReadOnlyList<TagCountDto>>(result);
    }

    public Task<PagedResult<JobSummaryDto>> GetJobsByStateAsync(
        string stateName, int page, int pageSize, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(stateName))
            return Task.FromResult(PagedResult<JobSummaryDto>.Empty(page, pageSize));

        var monitoringApi = _storage.GetMonitoringApi();
        var candidates = new List<JobSummaryDto>();

        // Use offset/limit directly from MonitoringApi
        ScanStateForFilter(monitoringApi, stateName, candidates, ct);

        return Task.FromResult(SortAndPaginate(candidates, page, pageSize));
    }

    public Task<IReadOnlyList<SlowestJobDto>> GetSlowestJobsAsync(
        int count, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var monitoringApi = _storage.GetMonitoringApi();
        var candidates = new List<SlowestJobDto>();

        int offset = 0;
        while (!ct.IsCancellationRequested && candidates.Count < SafetyCap)
        {
            var batch = monitoringApi.SucceededJobs(offset, BatchSize);
            if (batch == null || batch.Count == 0)
                break;

            foreach (var entry in batch)
            {
                if (candidates.Count >= SafetyCap)
                    break;

                var dto = entry.Value;
                if (dto == null)
                    continue;

                // Filter by time range
                if (dto.SucceededAt.HasValue)
                {
                    var succeededAt = new DateTimeOffset(dto.SucceededAt.Value, TimeSpan.Zero);
                    if (succeededAt < from || succeededAt > to)
                        continue;
                }
                else
                {
                    continue; // Skip jobs without a completion time
                }

                // Get duration from TotalDuration (PerformanceDuration in ms)
                if (dto.TotalDuration.HasValue && dto.TotalDuration.Value > 0)
                {
                    string jobName = "Unknown";
                    if (dto.Job != null)
                    {
                        jobName = $"{dto.Job.Type.Name}.{dto.Job.Method.Name}";
                    }

                    candidates.Add(new SlowestJobDto
                    {
                        JobId = entry.Key,
                        JobName = jobName,
                        DurationMs = (double)dto.TotalDuration.Value,
                        CompletedAt = dto.SucceededAt
                    });
                }
            }

            offset += BatchSize;
        }

        // Sort by duration descending and take top N
        candidates.Sort((a, b) => b.DurationMs.CompareTo(a.DurationMs));
        var result = candidates.Take(count).ToList();

        return Task.FromResult<IReadOnlyList<SlowestJobDto>>(result);
    }

    // ─── Private Helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Sorts candidates by CreatedAt descending and applies pagination.
    /// </summary>
    private static PagedResult<JobSummaryDto> SortAndPaginate(
        List<JobSummaryDto> candidates, int page, int pageSize)
    {
        candidates.Sort((a, b) =>
        {
            var aTime = a.CreatedAt ?? DateTime.MinValue;
            var bTime = b.CreatedAt ?? DateTime.MinValue;
            return bTime.CompareTo(aTime);
        });

        var skip = (page - 1) * pageSize;
        var items = candidates.Skip(skip).Take(pageSize).ToList();

        return new PagedResult<JobSummaryDto>
        {
            Items = items,
            TotalCount = candidates.Count,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>
    /// Scans a specific state for jobs matching the name query (case-insensitive substring).
    /// </summary>
    private void ScanStateForNameMatch(
        IMonitoringApi monitoringApi,
        string state,
        string query,
        List<JobSummaryDto> candidates,
        CancellationToken ct)
    {
        switch (state.ToLowerInvariant())
        {
            case "enqueued":
                ScanEnqueuedForName(monitoringApi, query, candidates, ct);
                break;
            case "processing":
                ScanBatchedState(monitoringApi.ProcessingJobs, "Processing", query, candidates, ct,
                    (dto) => dto?.Job, (dto) => dto?.StartedAt, (dto) => dto?.Job?.Queue);
                break;
            case "scheduled":
                ScanBatchedState(monitoringApi.ScheduledJobs, "Scheduled", query, candidates, ct,
                    (dto) => dto?.Job, (dto) => dto?.ScheduledAt, (dto) => dto?.Job?.Queue);
                break;
            case "succeeded":
                ScanSucceededForName(monitoringApi, query, candidates, ct);
                break;
            case "failed":
                ScanFailedForName(monitoringApi, query, candidates, ct);
                break;
            case "deleted":
                ScanDeletedForName(monitoringApi, query, candidates, ct);
                break;
        }
    }

    private void ScanEnqueuedForName(
        IMonitoringApi monitoringApi,
        string query,
        List<JobSummaryDto> candidates,
        CancellationToken ct)
    {
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
                        candidates.Add(new JobSummaryDto
                        {
                            JobId = entry.Key,
                            JobName = $"{dto.Job.Type.Name}.{dto.Job.Method.Name}",
                            State = "Enqueued",
                            Queue = queue.Name,
                            CreatedAt = dto.EnqueuedAt,
                            LastStateChange = dto.EnqueuedAt
                        });
                    }
                }

                from += BatchSize;
            }
        }
    }

    private void ScanSucceededForName(
        IMonitoringApi monitoringApi,
        string query,
        List<JobSummaryDto> candidates,
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
                    candidates.Add(new JobSummaryDto
                    {
                        JobId = entry.Key,
                        JobName = $"{dto.Job.Type.Name}.{dto.Job.Method.Name}",
                        State = "Succeeded",
                        Queue = dto.Job.Queue,
                        CreatedAt = dto.SucceededAt,
                        LastStateChange = dto.SucceededAt,
                        DurationMs = dto.TotalDuration.HasValue ? (double)dto.TotalDuration.Value : null
                    });
                }
            }

            from += BatchSize;
        }
    }

    private void ScanFailedForName(
        IMonitoringApi monitoringApi,
        string query,
        List<JobSummaryDto> candidates,
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
                    candidates.Add(new JobSummaryDto
                    {
                        JobId = entry.Key,
                        JobName = $"{dto.Job.Type.Name}.{dto.Job.Method.Name}",
                        State = "Failed",
                        Queue = dto.Job.Queue,
                        CreatedAt = dto.FailedAt,
                        LastStateChange = dto.FailedAt,
                        ExceptionType = dto.ExceptionType,
                        ExceptionMessage = dto.ExceptionMessage
                    });
                }
            }

            from += BatchSize;
        }
    }

    private void ScanDeletedForName(
        IMonitoringApi monitoringApi,
        string query,
        List<JobSummaryDto> candidates,
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
                    candidates.Add(new JobSummaryDto
                    {
                        JobId = entry.Key,
                        JobName = $"{dto.Job.Type.Name}.{dto.Job.Method.Name}",
                        State = "Deleted",
                        Queue = dto.Job.Queue,
                        CreatedAt = dto.DeletedAt,
                        LastStateChange = dto.DeletedAt
                    });
                }
            }

            from += BatchSize;
        }
    }

    /// <summary>
    /// Generic batch scan helper for ProcessingJobs and ScheduledJobs.
    /// </summary>
    private void ScanBatchedState<TDto>(
        Func<int, int, JobList<TDto>> fetchBatch,
        string stateName,
        string query,
        List<JobSummaryDto> candidates,
        CancellationToken ct,
        Func<TDto, Job> getJob,
        Func<TDto, DateTime?> getTimestamp,
        Func<TDto, string> getQueue)
    {
        int from = 0;
        while (!ct.IsCancellationRequested && candidates.Count < SafetyCap)
        {
            var batch = fetchBatch(from, BatchSize);
            if (batch == null || batch.Count == 0)
                break;

            foreach (var entry in batch)
            {
                if (candidates.Count >= SafetyCap)
                    break;

                var dto = entry.Value;
                var job = getJob(dto);
                if (job == null)
                    continue;

                if (MatchesNameQuery(job, query))
                {
                    candidates.Add(new JobSummaryDto
                    {
                        JobId = entry.Key,
                        JobName = $"{job.Type.Name}.{job.Method.Name}",
                        State = stateName,
                        Queue = getQueue(dto),
                        CreatedAt = getTimestamp(dto),
                        LastStateChange = getTimestamp(dto)
                    });
                }
            }

            from += BatchSize;
        }
    }

    /// <summary>
    /// Scans a state collecting ALL jobs (no name filter) for GetJobsWithFilterAsync / GetJobsByStateAsync.
    /// </summary>
    private void ScanStateForFilter(
        IMonitoringApi monitoringApi,
        string state,
        List<JobSummaryDto> candidates,
        CancellationToken ct)
    {
        switch (state.ToLowerInvariant())
        {
            case "enqueued":
                ScanEnqueuedAll(monitoringApi, candidates, ct);
                break;
            case "processing":
                ScanProcessingAll(monitoringApi, candidates, ct);
                break;
            case "scheduled":
                ScanScheduledAll(monitoringApi, candidates, ct);
                break;
            case "succeeded":
                ScanSucceededAll(monitoringApi, candidates, ct);
                break;
            case "failed":
                ScanFailedAll(monitoringApi, candidates, ct);
                break;
            case "deleted":
                ScanDeletedAll(monitoringApi, candidates, ct);
                break;
        }
    }

    private void ScanEnqueuedAll(IMonitoringApi monitoringApi, List<JobSummaryDto> candidates, CancellationToken ct)
    {
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
                    string jobName = "Unknown";
                    string q = queue.Name;
                    if (dto?.Job != null)
                    {
                        jobName = $"{dto.Job.Type.Name}.{dto.Job.Method.Name}";
                    }

                    candidates.Add(new JobSummaryDto
                    {
                        JobId = entry.Key,
                        JobName = jobName,
                        State = "Enqueued",
                        Queue = q,
                        CreatedAt = dto?.EnqueuedAt,
                        LastStateChange = dto?.EnqueuedAt
                    });
                }

                from += BatchSize;
            }
        }
    }

    private void ScanProcessingAll(IMonitoringApi monitoringApi, List<JobSummaryDto> candidates, CancellationToken ct)
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
                string jobName = "Unknown";
                string queue = null;
                if (dto?.Job != null)
                {
                    jobName = $"{dto.Job.Type.Name}.{dto.Job.Method.Name}";
                    queue = dto.Job.Queue;
                }

                candidates.Add(new JobSummaryDto
                {
                    JobId = entry.Key,
                    JobName = jobName,
                    State = "Processing",
                    Queue = queue,
                    CreatedAt = dto?.StartedAt,
                    LastStateChange = dto?.StartedAt
                });
            }

            from += BatchSize;
        }
    }

    private void ScanScheduledAll(IMonitoringApi monitoringApi, List<JobSummaryDto> candidates, CancellationToken ct)
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
                string jobName = "Unknown";
                string queue = null;
                if (dto?.Job != null)
                {
                    jobName = $"{dto.Job.Type.Name}.{dto.Job.Method.Name}";
                    queue = dto.Job.Queue;
                }

                candidates.Add(new JobSummaryDto
                {
                    JobId = entry.Key,
                    JobName = jobName,
                    State = "Scheduled",
                    Queue = queue,
                    CreatedAt = dto?.ScheduledAt,
                    LastStateChange = dto?.ScheduledAt
                });
            }

            from += BatchSize;
        }
    }

    private void ScanSucceededAll(IMonitoringApi monitoringApi, List<JobSummaryDto> candidates, CancellationToken ct)
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
                string jobName = "Unknown";
                string queue = null;
                if (dto?.Job != null)
                {
                    jobName = $"{dto.Job.Type.Name}.{dto.Job.Method.Name}";
                    queue = dto.Job.Queue;
                }

                candidates.Add(new JobSummaryDto
                {
                    JobId = entry.Key,
                    JobName = jobName,
                    State = "Succeeded",
                    Queue = queue,
                    CreatedAt = dto?.SucceededAt,
                    LastStateChange = dto?.SucceededAt,
                    DurationMs = dto?.TotalDuration.HasValue == true ? (double)dto.TotalDuration.Value : null
                });
            }

            from += BatchSize;
        }
    }

    private void ScanFailedAll(IMonitoringApi monitoringApi, List<JobSummaryDto> candidates, CancellationToken ct)
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
                string jobName = "Unknown";
                string queue = null;
                if (dto?.Job != null)
                {
                    jobName = $"{dto.Job.Type.Name}.{dto.Job.Method.Name}";
                    queue = dto.Job.Queue;
                }

                candidates.Add(new JobSummaryDto
                {
                    JobId = entry.Key,
                    JobName = jobName,
                    State = "Failed",
                    Queue = queue,
                    CreatedAt = dto?.FailedAt,
                    LastStateChange = dto?.FailedAt,
                    ExceptionType = dto?.ExceptionType,
                    ExceptionMessage = dto?.ExceptionMessage
                });
            }

            from += BatchSize;
        }
    }

    private void ScanDeletedAll(IMonitoringApi monitoringApi, List<JobSummaryDto> candidates, CancellationToken ct)
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
                string jobName = "Unknown";
                string queue = null;
                if (dto?.Job != null)
                {
                    jobName = $"{dto.Job.Type.Name}.{dto.Job.Method.Name}";
                    queue = dto.Job.Queue;
                }

                candidates.Add(new JobSummaryDto
                {
                    JobId = entry.Key,
                    JobName = jobName,
                    State = "Deleted",
                    Queue = queue,
                    CreatedAt = dto?.DeletedAt,
                    LastStateChange = dto?.DeletedAt
                });
            }

            from += BatchSize;
        }
    }

    /// <summary>
    /// Applies JobFilterCriteria client-side to a list of candidates.
    /// All non-null criteria are combined with AND logic.
    /// </summary>
    private List<JobSummaryDto> ApplyFilterCriteria(
        List<JobSummaryDto> candidates, JobFilterCriteria criteria, CancellationToken ct)
    {
        if (criteria == null)
            return candidates;

        var filtered = candidates.AsEnumerable();

        // State filter (already applied during scanning if single state, but double-check)
        if (!string.IsNullOrWhiteSpace(criteria.State))
        {
            filtered = filtered.Where(j =>
                string.Equals(j.State, criteria.State, StringComparison.OrdinalIgnoreCase));
        }

        // Date range filter
        if (criteria.DateFrom.HasValue)
        {
            var dateFrom = criteria.DateFrom.Value.UtcDateTime;
            filtered = filtered.Where(j => j.CreatedAt.HasValue && j.CreatedAt.Value >= dateFrom);
        }
        if (criteria.DateTo.HasValue)
        {
            var dateTo = criteria.DateTo.Value.UtcDateTime;
            filtered = filtered.Where(j => j.CreatedAt.HasValue && j.CreatedAt.Value <= dateTo);
        }

        // Queue filter
        if (!string.IsNullOrWhiteSpace(criteria.Queue))
        {
            filtered = filtered.Where(j =>
                !string.IsNullOrEmpty(j.Queue) &&
                string.Equals(j.Queue, criteria.Queue, StringComparison.OrdinalIgnoreCase));
        }

        // Server filter — requires looking up job details
        if (!string.IsNullOrWhiteSpace(criteria.Server))
        {
            ct.ThrowIfCancellationRequested();
            var monitoringApi = _storage.GetMonitoringApi();
            filtered = filtered.Where(j =>
            {
                try
                {
                    var details = monitoringApi.JobDetails(j.JobId);
                    if (details?.History == null)
                        return false;

                    return details.History.Any(h =>
                        string.Equals(h.StateName, "Processing", StringComparison.OrdinalIgnoreCase) &&
                        h.Data != null &&
                        h.Data.TryGetValue("ServerId", out var serverId) &&
                        string.Equals(serverId, criteria.Server, StringComparison.OrdinalIgnoreCase));
                }
                catch
                {
                    return false;
                }
            });
        }

        // Duration filter
        if (criteria.MinDuration.HasValue || criteria.MaxDuration.HasValue)
        {
            double? minMs = criteria.MinDuration?.TotalMilliseconds;
            double? maxMs = criteria.MaxDuration?.TotalMilliseconds;

            filtered = filtered.Where(j =>
            {
                if (!j.DurationMs.HasValue)
                    return false;
                if (minMs.HasValue && j.DurationMs.Value < minMs.Value)
                    return false;
                if (maxMs.HasValue && j.DurationMs.Value > maxMs.Value)
                    return false;
                return true;
            });
        }

        // Tags filter (AND logic: job must have ALL specified tags)
        if (criteria.Tags != null && criteria.Tags.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var tagSet = new HashSet<string>(criteria.Tags, StringComparer.OrdinalIgnoreCase);

            filtered = filtered.Where(j =>
            {
                var jobTags = j.Tags;
                if (jobTags == null)
                {
                    try
                    {
                        jobTags = _tagsReader.GetJobTags(j.JobId);
                        j.Tags = jobTags;
                    }
                    catch
                    {
                        return false;
                    }
                }

                return tagSet.All(t => jobTags.Any(jt =>
                    string.Equals(jt, t, StringComparison.OrdinalIgnoreCase)));
            });
        }

        // Recurring job ID filter
        if (!string.IsNullOrWhiteSpace(criteria.RecurringJobId))
        {
            ct.ThrowIfCancellationRequested();
            using var connection = _storage.GetReadOnlyConnection();

            filtered = filtered.Where(j =>
            {
                try
                {
                    if (connection is JobStorageConnection storageConnection)
                    {
                        var recurringJobId = storageConnection.GetJobParameter(j.JobId, "RecurringJobId");
                        return string.Equals(recurringJobId, criteria.RecurringJobId, StringComparison.OrdinalIgnoreCase);
                    }
                    return false;
                }
                catch
                {
                    return false;
                }
            });
        }

        return filtered.ToList();
    }

    /// <summary>
    /// Checks if a job's type name or method name contains the query as a case-insensitive substring.
    /// </summary>
    private static bool MatchesNameQuery(Job job, string query)
    {
        var typeName = job.Type?.Name;
        var methodName = job.Method?.Name;

        if (typeName != null && typeName.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        if (methodName != null && methodName.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
