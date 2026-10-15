using a2n.Hangfire.Dashboard.Helpers;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Storage;
using Hangfire;
using Hangfire.Common;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Fallback IStorageQueryProvider that uses Hangfire's IMonitoringApi.
/// Loads jobs in batches, applies client-side filtering, and paginates in memory.
/// Used when no dedicated storage adapter (SQL Server, PostgreSQL) is registered.
/// </summary>
public class GenericQueryProvider : IStorageQueryProvider
{
    private const int BatchSize = 100;
    private const int SafetyCap = 1000;
    private static readonly string[] AllStates =
        { "Enqueued", "Processing", "Scheduled", "Succeeded", "Failed", "Deleted" };

    private readonly JobStorage _storage;
    private readonly TagsDataReader _tagsReader;
    private readonly IFailedJobPageSource _failedJobs;

    /// <param name="storage">The Hangfire storage.</param>
    /// <param name="tagsReader">Reads tags for tag filters.</param>
    /// <param name="failedJobs">
    /// Optional page source for failed jobs, used for search and grouping instead of
    /// <see cref="IMonitoringApi.FailedJobs"/> when a storage adapter registers one.
    /// </param>
    public GenericQueryProvider(JobStorage storage, TagsDataReader tagsReader, IFailedJobPageSource failedJobs = null)
    {
        _storage = storage;
        _tagsReader = tagsReader;
        _failedJobs = failedJobs;
    }

    /// <inheritdoc />
    public Task<PagedResult<JobSummaryDto>> GetJobsWithFilterAsync(
        JobFilterCriteria criteria, int page, int pageSize, CancellationToken ct)
    {
        if (criteria == null || !criteria.HasAnyCriteria())
            return Task.FromResult(PagedResult<JobSummaryDto>.Empty(page, pageSize));

        var monitoringApi = _storage.GetMonitoringApi();
        var candidates = new List<JobSummaryDto>();

        // Determine which states to scan
        var states = criteria.GetEffectiveStates();
        var statesToScan = states.Count > 0 ? states : (IReadOnlyList<string>)AllStates;

        // The fingerprint is a job parameter, so it needs a connection; none is opened without it.
        // SafetyCap counts matches, not scanned jobs: for a rare exception every scanned job costs
        // one parameter read (a round trip on Redis) until the cap or the timeout. So the token is
        // checked before each read, not only between batches.
        var fingerprint = criteria.EffectiveFailureFingerprint;
        using var connection = fingerprint != null ? _storage.GetReadOnlyConnection() : null;
        var filter = new ScanFilter(criteria.JobNamePattern, criteria.ArgumentsPattern, fingerprint, connection, ct);

        foreach (var state in statesToScan)
        {
            if (ct.IsCancellationRequested || candidates.Count >= SafetyCap)
                break;

            ScanState(monitoringApi, state, filter, candidates, ct);
        }

        // A scan stopped by the timeout between batches would otherwise come back as if complete;
        // with the fingerprint, where a stop is likely, it is reported as a timeout instead.
        if (fingerprint != null)
            ct.ThrowIfCancellationRequested();

        // Scanning stops at SafetyCap candidates, so reaching it means there may be more matches.
        var hitScanCap = candidates.Count >= SafetyCap;

        // Apply client-side filters
        var filtered = ApplyFilters(candidates, criteria, ct);

        var result = SortAndPaginate(filtered, page, pageSize);
        result.TotalCountIsLowerBound = hitScanCap;
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<PagedResult<JobSummaryDto>> GetJobsByTagAsync(
        string tag, int page, int pageSize, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return Task.FromResult(PagedResult<JobSummaryDto>.Empty(page, pageSize));

        var allTags = _tagsReader.GetAllTags();
        var matchedTag = allTags.FirstOrDefault(t =>
            string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));

        if (matchedTag == null)
            return Task.FromResult(PagedResult<JobSummaryDto>.Empty(page, pageSize));

        ct.ThrowIfCancellationRequested();

        var totalJobCount = _tagsReader.GetJobCountByTag(matchedTag);
        if (totalJobCount == 0)
            return Task.FromResult(PagedResult<JobSummaryDto>.Empty(page, pageSize));

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

            // From the invocation data when the job's type can't be loaded (issue #70).
            var jobName = JobNameHelper.GetTypeAndMethod(jobData.Job, jobData.InvocationData) ?? "Unknown";
            var queue = QueueOf(jobData.Job, jobData.InvocationData);

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

    /// <inheritdoc />
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

        result.Sort((a, b) => b.Count.CompareTo(a.Count));
        return Task.FromResult<IReadOnlyList<TagCountDto>>(result);
    }

    /// <inheritdoc />
    public Task<PagedResult<JobSummaryDto>> GetJobsByStateAsync(
        string stateName, int page, int pageSize, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(stateName))
            return Task.FromResult(PagedResult<JobSummaryDto>.Empty(page, pageSize));

        var monitoringApi = _storage.GetMonitoringApi();
        var candidates = new List<JobSummaryDto>();

        ScanState(monitoringApi, stateName, ScanFilter.None, candidates, ct);

        return Task.FromResult(SortAndPaginate(candidates, page, pageSize));
    }

    /// <inheritdoc />
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
                if (dto == null || !dto.SucceededAt.HasValue || !dto.TotalDuration.HasValue)
                    continue;

                var succeededAt = new DateTimeOffset(dto.SucceededAt.Value, TimeSpan.Zero);
                if (succeededAt < from || succeededAt > to)
                    continue;

                if (dto.TotalDuration.Value > 0)
                {
                    var jobName = JobNameHelper.GetTypeAndMethod(dto.Job, dto.InvocationData) ?? "Unknown";

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

        candidates.Sort((a, b) => b.DurationMs.CompareTo(a.DurationMs));
        var result = candidates.Take(count).ToList();

        return Task.FromResult<IReadOnlyList<SlowestJobDto>>(result);
    }

    /// <inheritdoc />
    public bool SupportsFailureGrouping => true;

    /// <inheritdoc />
    /// <remarks>
    /// Without SQL access to job parameters, every fingerprint is computed here from the failed job's
    /// state, with the same function the filter uses, so it equals the value the filter stored. The
    /// newest <see cref="JobFilterCriteria.CountLimit"/> failed jobs are read; beyond that the result
    /// is partial.
    /// </remarks>
    public async Task<FailureGroupsResult> GetFailureGroupsAsync(int labelCount, CancellationToken ct)
    {
        var monitoringApi = _storage.GetMonitoringApi();
        var failedCount = monitoringApi.FailedCount();

        var read = new List<(string JobId, FailedJobDto Dto)>();
        while (read.Count < JobFilterCriteria.CountLimit)
        {
            ct.ThrowIfCancellationRequested();
            var batch = FetchFailed(monitoringApi, read.Count, Math.Min(BatchSize, JobFilterCriteria.CountLimit - read.Count));
            if (batch == null || batch.Count == 0)
                break;
            read.AddRange(batch.Select(entry => (entry.Key, entry.Value)));
        }

        // A job whose type can't be loaded is kept, as the search scans keep it (issue #70): the
        // fingerprint only needs the state, and a dashboard deployed apart from the job host often
        // can't resolve job types. The job type is still known from the stored invocation data.
        //
        // Storages differ in which end of the list is newest, so jobs are ordered by when they failed;
        // a job without that time counts as the oldest.
        var computed = read
            .Select((entry, index) => (entry.JobId, entry.Dto, Index: index))
            .Where(e => e.Dto != null)
            .OrderByDescending(e => e.Dto.FailedAt ?? DateTime.MinValue)
            .ThenBy(e => e.Index)
            .Select((e, rank) => new ComputedFailure(
                e.JobId,
                read.Count - rank,
                FailureFingerprint.Compute(
                    e.Dto.ExceptionType, e.Dto.ExceptionMessage, e.Dto.ExceptionDetails,
                    e.Dto.InvocationData?.Type ?? e.Dto.Job?.Type.FullName),
                e.Dto.FailedAt))
            .ToList();

        var result = await FailureGrouping.BuildAsync(
            stored: null, unfingerprintedCount: failedCount, computed, labelCount, loadNewestJobs: null, ct);

        // Every job that was read counts as covered, including one whose details the storage no
        // longer had: it can't be fingerprinted, but reading further wouldn't change that.
        result.FallbackCount = read.Count;
        // Stored fingerprints are never read here, so registering the filter wouldn't change the counts.
        result.UsesStoredFingerprints = false;
        return result;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<JobSummaryDto>> GetFailureGroupJobsAsync(
        FailureGroupDto group, int count, CancellationToken ct)
    {
        var jobs = new List<JobSummaryDto>();
        if (group == null || count <= 0)
            return Task.FromResult<IReadOnlyList<JobSummaryDto>>(jobs);

        using var connection = _storage.GetReadOnlyConnection();
        foreach (var jobId in group.ComputedJobIds ?? Array.Empty<string>())
        {
            if (jobs.Count >= count)
                break;
            ct.ThrowIfCancellationRequested();

            // The group lists the jobs as they were when it was read; skip any that has left the
            // Failed state since.
            var jobData = connection.GetJobData(jobId);
            if (jobData == null || !string.Equals(jobData.State, "Failed", StringComparison.OrdinalIgnoreCase))
                continue;

            var state = connection.GetStateData(jobId);
            var invocationData = jobData.InvocationData
                ?? (jobData.Job != null ? InvocationData.SerializeJob(jobData.Job) : null);

            jobs.Add(new JobSummaryDto
            {
                JobId = jobId,
                JobName = JobNameHelper.GetDisplayName(jobData.Job, invocationData),
                State = jobData.State,
                Queue = QueueOf(jobData.Job, invocationData),
                CreatedAt = jobData.CreatedAt,
                LastStateChange = FailureGrouping.ReadFailedAt(state?.Data, null),
                ExceptionType = GetValue(state?.Data, "ExceptionType"),
                ExceptionMessage = GetValue(state?.Data, "ExceptionMessage"),
                Reason = state?.Reason,
                InvocationData = invocationData,
            });
        }

        return Task.FromResult<IReadOnlyList<JobSummaryDto>>(jobs);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Every member of a group here is one the grouping read (<see cref="FailureGroupDto.ComputedJobIds"/>),
    /// so this only checks which of them are still failed, one state lookup per job.
    /// </remarks>
    public Task<IReadOnlyList<string>> GetFailureGroupJobIdsAsync(FailureGroupDto group, CancellationToken ct)
    {
        var ids = new List<string>();
        if (group == null)
            return Task.FromResult<IReadOnlyList<string>>(ids);

        using var connection = _storage.GetReadOnlyConnection();
        foreach (var jobId in group.ComputedJobIds ?? Array.Empty<string>())
        {
            ct.ThrowIfCancellationRequested();
            var state = connection.GetStateData(jobId);
            if (string.Equals(state?.Name, "Failed", StringComparison.OrdinalIgnoreCase))
                ids.Add(jobId);
        }

        return Task.FromResult<IReadOnlyList<string>>(ids);
    }

    private JobList<FailedJobDto> FetchFailed(IMonitoringApi monitoringApi, int from, int count)
        => _failedJobs != null ? _failedJobs.GetFailedJobs(from, count) : monitoringApi.FailedJobs(from, count);

    private static string GetValue(IDictionary<string, string> data, string key)
        => data != null && data.TryGetValue(key, out var value) ? value : null;

    // ─── Private Helpers ───────────────────────────────────────────────────────

    private static PagedResult<JobSummaryDto> SortAndPaginate(List<JobSummaryDto> candidates, int page, int pageSize)
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
    /// Scans a state for jobs, optionally filtering by name and argument patterns.
    /// </summary>
    private void ScanState(IMonitoringApi monitoringApi, string state, ScanFilter filter,
        List<JobSummaryDto> candidates, CancellationToken ct)
    {
        switch (state.ToLowerInvariant())
        {
            case "enqueued":
                ScanEnqueued(monitoringApi, filter, candidates, ct);
                break;
            case "processing":
                ScanBatched(monitoringApi.ProcessingJobs, "Processing", filter, candidates, ct,
                    dto => dto?.Job, dto => dto?.InvocationData, dto => dto?.StartedAt, dto => dto?.Job?.Queue);
                break;
            case "scheduled":
                ScanBatched(monitoringApi.ScheduledJobs, "Scheduled", filter, candidates, ct,
                    dto => dto?.Job, dto => dto?.InvocationData, dto => dto?.ScheduledAt, dto => dto?.Job?.Queue);
                break;
            case "succeeded":
                ScanSucceeded(monitoringApi, filter, candidates, ct);
                break;
            case "failed":
                ScanFailed(monitoringApi, filter, candidates, ct);
                break;
            case "deleted":
                ScanBatched(monitoringApi.DeletedJobs, "Deleted", filter, candidates, ct,
                    dto => dto?.Job, dto => dto?.InvocationData, dto => dto?.DeletedAt, dto => dto?.Job?.Queue);
                break;
        }
    }

    private void ScanEnqueued(IMonitoringApi monitoringApi, ScanFilter filter,
        List<JobSummaryDto> candidates, CancellationToken ct)
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
                    if (candidates.Count >= SafetyCap) break;
                    var dto = entry.Value;
                    if (!IsSearchable(dto?.Job, dto?.InvocationData)) continue;
                    // A job stays in its queue until a worker fetches it, even after it moves to
                    // another state (deleted or succeeded while enqueued). The scan of that state
                    // finds it, so listing it here too would return it twice.
                    if (!dto.InEnqueuedState) continue;
                    if (!filter.Matches(entry.Key, dto.Job, dto.InvocationData)) continue;

                    candidates.Add(new JobSummaryDto
                    {
                        JobId = entry.Key,
                        JobName = JobNameHelper.GetTypeAndMethod(dto.Job, dto.InvocationData),
                        State = "Enqueued",
                        Queue = queue.Name,
                        CreatedAt = dto.EnqueuedAt,
                        LastStateChange = dto.EnqueuedAt
                    });
                }
                from += BatchSize;
            }
        }
    }

    private void ScanSucceeded(IMonitoringApi monitoringApi, ScanFilter filter,
        List<JobSummaryDto> candidates, CancellationToken ct)
    {
        int from = 0;
        while (!ct.IsCancellationRequested && candidates.Count < SafetyCap)
        {
            var batch = monitoringApi.SucceededJobs(from, BatchSize);
            if (batch == null || batch.Count == 0) break;

            foreach (var entry in batch)
            {
                if (candidates.Count >= SafetyCap) break;
                var dto = entry.Value;
                if (!IsSearchable(dto?.Job, dto?.InvocationData)) continue;
                if (!filter.Matches(entry.Key, dto.Job, dto.InvocationData)) continue;

                candidates.Add(new JobSummaryDto
                {
                    JobId = entry.Key,
                    JobName = JobNameHelper.GetTypeAndMethod(dto.Job, dto.InvocationData),
                    State = "Succeeded",
                    Queue = QueueOf(dto.Job, dto.InvocationData),
                    CreatedAt = dto.SucceededAt,
                    LastStateChange = dto.SucceededAt,
                    DurationMs = dto.TotalDuration.HasValue ? (double)dto.TotalDuration.Value : null
                });
            }
            from += BatchSize;
        }
    }

    private void ScanFailed(IMonitoringApi monitoringApi, ScanFilter filter,
        List<JobSummaryDto> candidates, CancellationToken ct)
    {
        int from = 0;
        while (!ct.IsCancellationRequested && candidates.Count < SafetyCap)
        {
            var batch = FetchFailed(monitoringApi, from, BatchSize);
            if (batch == null || batch.Count == 0) break;

            foreach (var entry in batch)
            {
                if (candidates.Count >= SafetyCap) break;
                var dto = entry.Value;
                if (!IsSearchable(dto?.Job, dto?.InvocationData)) continue;
                if (!filter.Matches(entry.Key, dto.Job, dto.InvocationData)) continue;

                candidates.Add(new JobSummaryDto
                {
                    JobId = entry.Key,
                    JobName = JobNameHelper.GetTypeAndMethod(dto.Job, dto.InvocationData),
                    State = "Failed",
                    Queue = QueueOf(dto.Job, dto.InvocationData),
                    CreatedAt = dto.FailedAt,
                    LastStateChange = dto.FailedAt,
                    ExceptionType = dto.ExceptionType,
                    ExceptionMessage = dto.ExceptionMessage
                });
            }
            from += BatchSize;
        }
    }

    private void ScanBatched<TDto>(Func<int, int, JobList<TDto>> fetchBatch,
        string stateName, ScanFilter filter, List<JobSummaryDto> candidates, CancellationToken ct,
        Func<TDto, Job> getJob, Func<TDto, InvocationData> getInvocationData,
        Func<TDto, DateTime?> getTimestamp, Func<TDto, string> getQueue)
    {
        int from = 0;
        while (!ct.IsCancellationRequested && candidates.Count < SafetyCap)
        {
            var batch = fetchBatch(from, BatchSize);
            if (batch == null || batch.Count == 0) break;

            foreach (var entry in batch)
            {
                if (candidates.Count >= SafetyCap) break;
                var dto = entry.Value;
                var job = getJob(dto);
                var invocationData = getInvocationData(dto);
                if (!IsSearchable(job, invocationData)) continue;
                if (!filter.Matches(entry.Key, job, invocationData)) continue;

                candidates.Add(new JobSummaryDto
                {
                    JobId = entry.Key,
                    JobName = JobNameHelper.GetTypeAndMethod(job, invocationData),
                    State = stateName,
                    Queue = getQueue(dto) ?? invocationData?.Queue,
                    CreatedAt = getTimestamp(dto),
                    LastStateChange = getTimestamp(dto)
                });
            }
            from += BatchSize;
        }
    }

    /// <summary>
    /// Applies filter criteria client-side (for GenericQueryProvider fallback).
    /// </summary>
    private List<JobSummaryDto> ApplyFilters(List<JobSummaryDto> candidates, JobFilterCriteria criteria, CancellationToken ct)
    {
        var filtered = candidates.AsEnumerable();

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

        if (!string.IsNullOrWhiteSpace(criteria.Queue))
        {
            filtered = filtered.Where(j =>
                !string.IsNullOrEmpty(j.Queue) &&
                string.Equals(j.Queue, criteria.Queue, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(criteria.ExceptionPattern))
        {
            filtered = filtered.Where(j =>
                (!string.IsNullOrEmpty(j.ExceptionType) &&
                 j.ExceptionType.Contains(criteria.ExceptionPattern, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(j.ExceptionMessage) &&
                 j.ExceptionMessage.Contains(criteria.ExceptionPattern, StringComparison.OrdinalIgnoreCase)));
        }

        if (criteria.MinDuration.HasValue || criteria.MaxDuration.HasValue)
        {
            double? minMs = criteria.MinDuration?.TotalMilliseconds;
            double? maxMs = criteria.MaxDuration?.TotalMilliseconds;

            filtered = filtered.Where(j =>
            {
                if (!j.DurationMs.HasValue) return false;
                if (minMs.HasValue && j.DurationMs.Value < minMs.Value) return false;
                if (maxMs.HasValue && j.DurationMs.Value > maxMs.Value) return false;
                return true;
            });
        }

        if (criteria.Tags != null && criteria.Tags.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var tagSet = new HashSet<string>(criteria.Tags, StringComparer.OrdinalIgnoreCase);

            filtered = filtered.Where(j =>
            {
                var jobTags = j.Tags;
                if (jobTags == null)
                {
                    try { jobTags = _tagsReader.GetJobTags(j.JobId); j.Tags = jobTags; }
                    catch { return false; }
                }
                return tagSet.All(t => jobTags.Any(jt =>
                    string.Equals(jt, t, StringComparison.OrdinalIgnoreCase)));
            });
        }

        if (!string.IsNullOrWhiteSpace(criteria.Server))
        {
            ct.ThrowIfCancellationRequested();
            var monitoringApi = _storage.GetMonitoringApi();
            filtered = filtered.Where(j =>
            {
                try
                {
                    var details = monitoringApi.JobDetails(j.JobId);
                    return details?.History?.Any(h =>
                        string.Equals(h.StateName, "Processing", StringComparison.OrdinalIgnoreCase) &&
                        h.Data != null &&
                        h.Data.TryGetValue("ServerId", out var serverId) &&
                        string.Equals(serverId, criteria.Server, StringComparison.OrdinalIgnoreCase)) == true;
                }
                catch { return false; }
            });
        }

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
                catch { return false; }
            });
        }

        return filtered.ToList();
    }

    /// <summary>
    /// Whether a scanned job can be listed. <c>Job</c> is null when the dashboard can't load the job's
    /// type, which is common when it runs in a process that doesn't reference the job assemblies;
    /// such a job is still listed, named and matched from its invocation data (issue #70). Only a
    /// job with neither is skipped.
    /// </summary>
    private static bool IsSearchable(Job job, InvocationData invocationData)
        => job != null || invocationData != null;

    private static string QueueOf(Job job, InvocationData invocationData)
        => job?.Queue ?? invocationData?.Queue;

    private static bool MatchesName(Job job, InvocationData invocationData, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return true;

        var (typeName, methodName) = JobNameHelper.GetTypeAndMethodNames(job, invocationData);

        if (typeName != null && typeName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            return true;
        if (methodName != null && methodName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Matches against the serialized job arguments only, never the type or method name — the
    /// same scope the storage adapters use for their SQL predicate.
    /// </summary>
    private static bool MatchesArguments(Job job, InvocationData invocationData, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return true;

        var arguments = JobArgumentsRenderer.GetArgumentsText(job, invocationData);
        return arguments.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Patterns applied while scanning, before a job becomes a candidate. Filtering here rather
    /// than in <see cref="ApplyFilters"/> keeps the <see cref="SafetyCap"/> budget available for
    /// rows that can actually be returned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="FailureFingerprint"/> is checked last among these, since it is the only check that
    /// reads storage: one parameter lookup per job that passes the others.
    /// </para>
    /// <para>
    /// The date, server and duration filters are applied later, in <see cref="ApplyFilters"/>, so a
    /// job they drop may still have had its parameter read. Moving them into the scan would mean
    /// reading those values from each state's DTO here; with the timeout checked per read, the
    /// extra reads are bounded, so the scan is left as it is.
    /// </para>
    /// </remarks>
    private readonly record struct ScanFilter(string NamePattern, string ArgumentsPattern,
        string FailureFingerprint, IStorageConnection Connection, CancellationToken Ct)
    {
        /// <summary>No filtering — every scanned job becomes a candidate.</summary>
        public static ScanFilter None => default;

        public bool Matches(string jobId, Job job, InvocationData invocationData)
            => MatchesName(job, invocationData, NamePattern)
            && MatchesArguments(job, invocationData, ArgumentsPattern)
            && MatchesFailureFingerprint(jobId, FailureFingerprint, Connection, Ct);
    }

    /// <summary>
    /// Whether the job carries this stored fingerprint. The parameter is read raw: the filter stores
    /// it as a plain string, which the typed <c>GetJobParameter&lt;T&gt;</c> would try to parse as JSON.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// The search timed out or was cancelled; checked before each read, since each is a storage call.
    /// </exception>
    private static bool MatchesFailureFingerprint(string jobId, string fingerprint, IStorageConnection connection,
        CancellationToken ct)
    {
        if (fingerprint == null) return true;

        ct.ThrowIfCancellationRequested();
        var stored = connection.GetJobParameter(jobId, Helpers.FailureFingerprint.ParameterName);
        return string.Equals(stored, fingerprint, StringComparison.Ordinal);
    }
}
