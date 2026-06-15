using System.Text.Json;
using Hangfire;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using a2n.Hangfire.Dashboard.Internal;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Service that wraps Hangfire's IMonitoringApi for dashboard data access.
/// </summary>
public class HangfireMonitorService
{
    private readonly JobStorage _storage;
    private readonly AuditLogService _audit;
    private readonly DashboardUIOptions _options;
    private readonly JobMethodResolver _resolver;

    public HangfireMonitorService(
        JobStorage storage,
        AuditLogService audit = null,
        DashboardUIOptions options = null,
        JobMethodResolver resolver = null)
    {
        _storage = storage;
        _audit = audit;
        // Defaults keep the service usable when constructed without the Job Builder dependencies
        // (e.g. the existing DI factory). DI should supply the configured options and the singleton
        // resolver so the access gates and discovery cache behave correctly at runtime.
        _options = options ?? new DashboardUIOptions();
        _resolver = resolver ?? new JobMethodResolver();
    }

    /// <summary>
    /// Gets the underlying JobStorage instance for metadata access (version, connection info).
    /// </summary>
    public JobStorage GetStorage() => _storage;

    private IMonitoringApi GetMonitoringApi() => _storage.GetMonitoringApi();

    public StatisticsDto GetStatistics() => GetMonitoringApi().GetStatistics();

    public IList<ServerDto> GetServers() => GetMonitoringApi().Servers();

    public IList<QueueWithTopEnqueuedJobsDto> GetQueues() => GetMonitoringApi().Queues();

    public JobList<ProcessingJobDto> GetProcessingJobs(int from, int count)
        => GetMonitoringApi().ProcessingJobs(from, count);

    public JobList<ScheduledJobDto> GetScheduledJobs(int from, int count)
        => GetMonitoringApi().ScheduledJobs(from, count);

    public JobList<SucceededJobDto> GetSucceededJobs(int from, int count)
        => GetMonitoringApi().SucceededJobs(from, count);

    public JobList<FailedJobDto> GetFailedJobs(int from, int count)
        => GetMonitoringApi().FailedJobs(from, count);

    public JobList<DeletedJobDto> GetDeletedJobs(int from, int count)
        => GetMonitoringApi().DeletedJobs(from, count);

    public JobList<EnqueuedJobDto> GetEnqueuedJobs(string queue, int from, int count)
        => GetMonitoringApi().EnqueuedJobs(queue, from, count);

    public JobDetailsDto GetJobDetails(string jobId)
        => GetMonitoringApi().JobDetails(jobId);

    public long GetProcessingCount() => GetMonitoringApi().ProcessingCount();
    public long GetScheduledCount() => GetMonitoringApi().ScheduledCount();
    public long GetFailedCount() => GetMonitoringApi().FailedCount();
    public long GetSucceededListCount() => GetMonitoringApi().SucceededListCount();
    public long GetDeletedListCount() => GetMonitoringApi().DeletedListCount();
    public long GetEnqueuedCount(string queue) => GetMonitoringApi().EnqueuedCount(queue);

    public JobList<FetchedJobDto> GetFetchedJobs(string queue, int from, int count)
        => GetMonitoringApi().FetchedJobs(queue, from, count);

    public long GetFetchedCount(string queue) => GetMonitoringApi().FetchedCount(queue);

    public IDictionary<DateTime, long> GetHourlySucceededJobs()
        => GetMonitoringApi().HourlySucceededJobs();

    public IDictionary<DateTime, long> GetHourlyFailedJobs()
        => GetMonitoringApi().HourlyFailedJobs();

    public IDictionary<DateTime, long> GetSucceededByDatesCount()
        => GetMonitoringApi().SucceededByDatesCount();

    public IDictionary<DateTime, long> GetFailedByDatesCount()
        => GetMonitoringApi().FailedByDatesCount();

    public IReadOnlyList<RecurringJobDto> GetRecurringJobs()
    {
        using var connection = _storage.GetReadOnlyConnection();
        if (connection is JobStorageConnection storageConnection)
        {
            return storageConnection.GetRecurringJobs();
        }
        return [];
    }

    public long GetRecurringJobCount()
    {
        using var connection = _storage.GetReadOnlyConnection();
        if (connection is JobStorageConnection storageConnection)
        {
            return storageConnection.GetRecurringJobCount();
        }
        return 0;
    }

    /// <summary>
    /// Gets job IDs from a named set (e.g., "retries", "awaiting").
    /// </summary>
    public IReadOnlyList<string> GetSetJobIds(string setName, int from, int count)
    {
        using var connection = _storage.GetReadOnlyConnection();
        if (connection is JobStorageConnection storageConnection)
        {
            return storageConnection.GetRangeFromSet(setName, from, from + count - 1);
        }
        return [];
    }

    /// <summary>
    /// Gets count of items in a named set.
    /// </summary>
    public long GetSetCount(string setName)
    {
        using var connection = _storage.GetReadOnlyConnection();
        if (connection is JobStorageConnection storageConnection)
        {
            return storageConnection.GetSetCount(setName);
        }
        return 0;
    }

    // ===== Job Actions =====

    /// <summary>
    /// Requeue a job (change state to Enqueued).
    /// </summary>
    public bool RequeueJob(string jobId)
    {
        var client = new BackgroundJobClient(_storage);
        var ok = client.ChangeState(jobId, new EnqueuedState());
        if (ok) _audit?.Log(AuditAction.JobRequeued, target: jobId);
        return ok;
    }

    /// <summary>
    /// Delete a job (change state to Deleted).
    /// </summary>
    public bool DeleteJob(string jobId)
    {
        var client = new BackgroundJobClient(_storage);
        var ok = client.ChangeState(jobId, new DeletedState { Reason = "Deleted via Dashboard" });
        if (ok) _audit?.Log(AuditAction.JobDeleted, target: jobId);
        return ok;
    }

    /// <summary>
    /// Requeue multiple jobs.
    /// </summary>
    public int RequeueJobs(IEnumerable<string> jobIds)
    {
        var client = new BackgroundJobClient(_storage);
        var idList = jobIds?.ToList() ?? [];
        var count = 0;
        foreach (var jobId in idList)
        {
            if (client.ChangeState(jobId, new EnqueuedState()))
                count++;
        }
        if (idList.Count > 0)
        {
            _audit?.Log(AuditAction.JobsRequeuedBatch, metadata: new Dictionary<string, string>
            {
                ["requested"] = idList.Count.ToString(),
                ["succeeded"] = count.ToString(),
            });
        }
        return count;
    }

    /// <summary>
    /// Delete multiple jobs.
    /// </summary>
    public int DeleteJobs(IEnumerable<string> jobIds)
    {
        var client = new BackgroundJobClient(_storage);
        var idList = jobIds?.ToList() ?? [];
        var count = 0;
        foreach (var jobId in idList)
        {
            if (client.ChangeState(jobId, new DeletedState { Reason = "Deleted via Dashboard" }))
                count++;
        }
        if (idList.Count > 0)
        {
            _audit?.Log(AuditAction.JobsDeletedBatch, metadata: new Dictionary<string, string>
            {
                ["requested"] = idList.Count.ToString(),
                ["succeeded"] = count.ToString(),
            });
        }
        return count;
    }

    /// <summary>
    /// Creates a recurring job when no job with the request's id exists, otherwise updates the
    /// existing one (upsert by id), building typed <c>Args</c> from the supplied Parameter_JSON
    /// (Req 1.1, 1.4, 11.3). Access gates are enforced first and any failure leaves stored state
    /// unchanged, returning a <see cref="JobOperationResult"/> identifying the offending field/reason
    /// (Req 4.6, 4.7, 4.8, 13.5).
    /// </summary>
    /// <param name="request">The recurring job construction request from the Job Builder.</param>
    /// <returns>
    /// A successful result carrying the recurring job id, or a failed result whose
    /// <see cref="JobOperationResult.Error"/> and <see cref="JobOperationResult.FailedField"/>
    /// identify the reason. Storage is touched only after every gate, resolution, and conversion
    /// step succeeds.
    /// </returns>
    public JobOperationResult CreateOrUpdateRecurringJob(RecurringJobRequest request)
    {
        if (request is null)
            return new JobOperationResult(false, null, "A recurring job request is required.", null);

        // --- Gates first: reject before any resolution, conversion, or storage access. ---

        // Read-only mode disallows all mutating actions (Req 4.6).
        if (_options.IsReadOnly)
            return new JobOperationResult(false, request.JobId, "The dashboard is read-only.", null);

        // Job management must be enabled (Req 4.7).
        if (!_options.EnableJobManagement)
            return new JobOperationResult(false, request.JobId, "Job management is disabled.", null);

        // Arbitrary method invocation is an explicit opt-in (Req 4.8).
        if (request.IsCustomMethod && !_options.AllowArbitraryMethodInvocation)
            return new JobOperationResult(false, request.JobId, "Arbitrary method invocation is disabled.", null);

        // Job identifier must be present to upsert by id (Req 11.3).
        if (string.IsNullOrWhiteSpace(request.JobId))
            return new JobOperationResult(false, request.JobId, "A job identifier is required.", nameof(RecurringJobRequest.JobId));

        // --- Parse Parameter_JSON into the ordered argument elements (over Job_Parameters). ---
        List<JsonElement> args;
        try
        {
            args = ParseParameterJson(request.ParameterJson);
        }
        catch (JsonException)
        {
            return new JobOperationResult(false, request.JobId, "The parameter JSON is malformed.", nameof(RecurringJobRequest.ParameterJson));
        }

        if (args is null)
            return new JobOperationResult(false, request.JobId, "The parameter JSON must be a JSON array.", nameof(RecurringJobRequest.ParameterJson));

        // --- Resolve the target method overload (Req 1.5–1.8). ---
        var resolution = _resolver.ResolveMethod(request.TypeName, request.MethodName, args.Count, args);
        if (!resolution.Success)
        {
            var failedField = resolution.ErrorKind == MethodResolutionError.TypeNotFound
                ? nameof(RecurringJobRequest.TypeName)
                : nameof(RecurringJobRequest.MethodName);
            return new JobOperationResult(false, request.JobId, resolution.Error, failedField);
        }

        // --- Build the positional, typed Args array (Req 1.1–1.4). ---
        var build = JobArgumentConverter.BuildArgs(resolution.Method, args);
        if (!build.Success)
            return new JobOperationResult(false, request.JobId, build.Error, build.ParameterName);

        // --- Resolve the time zone before touching storage so a bad id leaves state unchanged. ---
        TimeZoneInfo timeZone;
        try
        {
            timeZone = string.IsNullOrEmpty(request.TimeZoneId)
                ? TimeZoneInfo.Utc
                : TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return new JobOperationResult(false, request.JobId, $"Time zone '{request.TimeZoneId}' could not be found.", nameof(RecurringJobRequest.TimeZoneId));
        }

        // Configured_Queue stored on the recurring job; default queue when none supplied (Req 13.5).
        var queue = string.IsNullOrWhiteSpace(request.Queue) ? "default" : request.Queue;

        // --- Upsert by id. Any storage/cron failure leaves stored state unchanged (Req 11.3). ---
        try
        {
            var job = new global::Hangfire.Common.Job(resolution.Method.DeclaringType, resolution.Method, build.Args);
            var manager = new RecurringJobManager(_storage);

#pragma warning disable CS0618 // QueueName is obsolete but no alternative overload available in 1.8.x
            manager.AddOrUpdate(request.JobId, job, request.Cron, new RecurringJobOptions
            {
                TimeZone = timeZone,
                QueueName = queue
            });
#pragma warning restore CS0618
        }
        catch (Exception ex)
        {
            return new JobOperationResult(false, request.JobId, ex.Message, null);
        }

        _audit?.Log(AuditAction.RecurringUpdated, target: request.JobId, metadata: new Dictionary<string, string>
        {
            ["typeName"] = request.TypeName ?? string.Empty,
            ["methodName"] = request.MethodName ?? string.Empty,
            ["cron"] = request.Cron ?? string.Empty,
            ["queue"] = queue,
            ["timeZone"] = string.IsNullOrEmpty(request.TimeZoneId) ? "UTC" : request.TimeZoneId,
        });

        return new JobOperationResult(true, request.JobId, null, null);
    }

    /// <summary>
    /// Enqueues a one-off job from the supplied request, building typed <c>Args</c> from the
    /// Parameter_JSON using the same resolver and converter as the recurring path so argument and
    /// queue handling stay in parity (Req 12.3, 12.4). Access gates are enforced first and any
    /// failure makes no state change, returning a <see cref="JobOperationResult"/> identifying the
    /// offending field/reason (Req 4.6, 4.8, 12.5). Unlike the recurring path this method does NOT
    /// enforce the recurring-admin gate, which is recurring-only.
    /// </summary>
    /// <param name="request">The enqueue request from the Job Builder.</param>
    /// <returns>
    /// A successful result carrying the newly created background job id, or a failed result whose
    /// <see cref="JobOperationResult.Error"/> and <see cref="JobOperationResult.FailedField"/>
    /// identify the reason. Storage is touched only after every gate, resolution, and conversion
    /// step succeeds.
    /// </returns>
    public JobOperationResult EnqueueJob(EnqueueJobRequest request)
    {
        if (request is null)
            return new JobOperationResult(false, null, "An enqueue job request is required.", null);

        // --- Gates first: reject before any resolution, conversion, or storage access. ---

        // Read-only mode disallows all mutating actions (Req 4.6).
        if (_options.IsReadOnly)
            return new JobOperationResult(false, null, "The dashboard is read-only.", null);

        // Arbitrary method invocation is an explicit opt-in (Req 4.8). The job-management gate is
        // intentionally NOT applied here — it governs recurring jobs only.
        if (request.IsCustomMethod && !_options.AllowArbitraryMethodInvocation)
            return new JobOperationResult(false, null, "Arbitrary method invocation is disabled.", null);

        // --- Parse Parameter_JSON into the ordered argument elements (over Job_Parameters). ---
        List<JsonElement> args;
        try
        {
            args = ParseParameterJson(request.ParameterJson);
        }
        catch (JsonException)
        {
            return new JobOperationResult(false, null, "The parameter JSON is malformed.", nameof(EnqueueJobRequest.ParameterJson));
        }

        if (args is null)
            return new JobOperationResult(false, null, "The parameter JSON must be a JSON array.", nameof(EnqueueJobRequest.ParameterJson));

        // --- Resolve the target method overload (Req 1.5–1.8, shared with the recurring path). ---
        var resolution = _resolver.ResolveMethod(request.TypeName, request.MethodName, args.Count, args);
        if (!resolution.Success)
        {
            var failedField = resolution.ErrorKind == MethodResolutionError.TypeNotFound
                ? nameof(EnqueueJobRequest.TypeName)
                : nameof(EnqueueJobRequest.MethodName);
            return new JobOperationResult(false, null, resolution.Error, failedField);
        }

        // --- Build the positional, typed Args array (Req 1.1–1.4, shared with the recurring path). ---
        var build = JobArgumentConverter.BuildArgs(resolution.Method, args);
        if (!build.Success)
            return new JobOperationResult(false, null, build.Error, build.ParameterName);

        // Default queue when none supplied (Req 12.2).
        var queue = string.IsNullOrWhiteSpace(request.Queue) ? "default" : request.Queue;

        // --- Enqueue. Any storage failure makes no state change (Req 12.5). ---
        string jobId;
        try
        {
            var job = new global::Hangfire.Common.Job(resolution.Method.DeclaringType, resolution.Method, build.Args);
            var client = new BackgroundJobClient(_storage);
            jobId = client.Create(job, new EnqueuedState(queue));
        }
        catch (Exception ex)
        {
            return new JobOperationResult(false, null, ex.Message, null);
        }

        _audit?.Log("job.enqueued", target: jobId, metadata: new Dictionary<string, string>
        {
            ["typeName"] = request.TypeName ?? string.Empty,
            ["methodName"] = request.MethodName ?? string.Empty,
            ["queue"] = queue,
        });

        // Return the new background job id on success (Req 12.3, 12.6).
        return new JobOperationResult(true, jobId, null, null);
    }

    /// <summary>
    /// Asynchronous wrapper over <see cref="CreateOrUpdateRecurringJob"/>. The upsert writes to
    /// storage through Hangfire's synchronous <c>RecurringJobManager</c>, so it is offloaded to the
    /// thread pool to avoid blocking the Blazor Server circuit while it runs. Behaviour, gating, and
    /// the returned <see cref="JobOperationResult"/> are identical to the synchronous overload.
    /// </summary>
    public Task<JobOperationResult> CreateOrUpdateRecurringJobAsync(RecurringJobRequest request)
        => Task.Run(() => CreateOrUpdateRecurringJob(request));

    /// <summary>
    /// Asynchronous wrapper over <see cref="EnqueueJob"/>. The enqueue writes to storage through
    /// Hangfire's synchronous <c>BackgroundJobClient</c>, so it is offloaded to the thread pool to
    /// avoid blocking the Blazor Server circuit; see <see cref="CreateOrUpdateRecurringJobAsync"/>.
    /// </summary>
    public Task<JobOperationResult> EnqueueJobAsync(EnqueueJobRequest request)
        => Task.Run(() => EnqueueJob(request));

    /// <summary>
    /// Parses a Parameter_JSON string into the ordered argument elements over the method's
    /// Job_Parameters. Returns an empty list for null/blank input, <c>null</c> when the top-level
    /// JSON value is not an array, and throws <see cref="JsonException"/> for malformed JSON.
    /// </summary>
    private static List<JsonElement> ParseParameterJson(string parameterJson)
    {
        if (string.IsNullOrWhiteSpace(parameterJson))
            return [];

        using var document = JsonDocument.Parse(parameterJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        var elements = new List<JsonElement>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            // Clone so the elements remain valid after the JsonDocument is disposed.
            elements.Add(element.Clone());
        }

        return elements;
    }

    /// <summary>
    /// Trigger a recurring job immediately.
    /// </summary>
    public void TriggerRecurringJob(string recurringJobId)
    {
        var manager = new RecurringJobManager(_storage);
        manager.Trigger(recurringJobId);
        _audit?.Log(AuditAction.RecurringTriggered, target: recurringJobId);
    }

    /// <summary>
    /// Remove a recurring job.
    /// </summary>
    public void RemoveRecurringJob(string recurringJobId)
    {
        var manager = new RecurringJobManager(_storage);
        manager.RemoveIfExists(recurringJobId);
        _audit?.Log(AuditAction.RecurringDeleted, target: recurringJobId);
    }

    /// <summary>
    /// Trigger multiple recurring jobs.
    /// </summary>
    public void TriggerRecurringJobs(IEnumerable<string> recurringJobIds)
    {
        var manager = new RecurringJobManager(_storage);
        var ids = recurringJobIds?.ToList() ?? [];
        foreach (var id in ids)
            manager.Trigger(id);
        if (ids.Count > 0)
        {
            _audit?.Log(AuditAction.RecurringTriggered, metadata: new Dictionary<string, string>
            {
                ["count"] = ids.Count.ToString(),
                ["ids"] = string.Join(",", ids.Take(20)) + (ids.Count > 20 ? $",+{ids.Count - 20}" : string.Empty),
            });
        }
    }

    /// <summary>
    /// Remove multiple recurring jobs.
    /// </summary>
    public void RemoveRecurringJobs(IEnumerable<string> recurringJobIds)
    {
        var manager = new RecurringJobManager(_storage);
        var ids = recurringJobIds?.ToList() ?? [];
        foreach (var id in ids)
            manager.RemoveIfExists(id);
        if (ids.Count > 0)
        {
            _audit?.Log(AuditAction.RecurringDeleted, metadata: new Dictionary<string, string>
            {
                ["count"] = ids.Count.ToString(),
                ["ids"] = string.Join(",", ids.Take(20)) + (ids.Count > 20 ? $",+{ids.Count - 20}" : string.Empty),
            });
        }
    }

    // ===== Recurring Job Start/Stop =====

    private const string StoppedJobsSet = "recurring:stopped";

    /// <summary>
    /// Stops a recurring job by removing it from the scheduler and storing its config.
    /// </summary>
    public void StopRecurringJob(string recurringJobId)
    {
        // Read the recurring job config before removing
        using var connection = _storage.GetReadOnlyConnection();
        if (connection is not JobStorageConnection storageConnection) return;

        var recurringJobs = storageConnection.GetRecurringJobs();
        var job = recurringJobs.FirstOrDefault(j => j.Id == recurringJobId);
        if (job is null) return;

        // Store the config in a hash so we can restore it later
        var hashKey = $"recurring:stopped:{recurringJobId}";
        var data = new Dictionary<string, string>
        {
            ["Cron"] = job.Cron ?? "",
            ["Queue"] = job.Queue ?? "default",
            ["TimeZoneId"] = job.TimeZoneId ?? "UTC",
            ["TypeName"] = job.Job?.Type.FullName ?? "",
            ["MethodName"] = job.Job?.Method.Name ?? "",
            ["StoppedAt"] = DateTime.UtcNow.ToString("O")
        };

        using var transaction = connection.CreateWriteTransaction();
        transaction.SetRangeInHash(hashKey, data.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value)));
        transaction.AddToSet(StoppedJobsSet, recurringJobId);
        transaction.Commit();

        // Remove the recurring job from the scheduler
        var manager = new RecurringJobManager(_storage);
        manager.RemoveIfExists(recurringJobId);

        _audit?.Log(AuditAction.RecurringStopped, target: recurringJobId);
    }

    /// <summary>
    /// Starts (restores) a previously stopped recurring job.
    /// </summary>
    public void StartRecurringJob(string recurringJobId)
    {
        using var connection = _storage.GetConnection();
        if (connection is not JobStorageConnection storageConnection) return;

        var hashKey = $"recurring:stopped:{recurringJobId}";
        var data = storageConnection.GetAllEntriesFromHash(hashKey);
        if (data is null || data.Count == 0) return;

        var typeName = data.GetValueOrDefault("TypeName") ?? "";
        var methodName = data.GetValueOrDefault("MethodName") ?? "";
        var cron = data.GetValueOrDefault("Cron") ?? "* * * * *";
        var queue = data.GetValueOrDefault("Queue") ?? "default";
        var timeZoneId = data.GetValueOrDefault("TimeZoneId") ?? "UTC";

        // Re-create the recurring job from the stored (name-based) config.
        RestoreRecurringJobByName(recurringJobId, typeName, methodName, cron, queue, timeZoneId);

        // Clean up the stopped state
        using var transaction = connection.CreateWriteTransaction();
        transaction.RemoveFromSet(StoppedJobsSet, recurringJobId);
        // Mark the hash as deleted by overwriting with a flag
        transaction.SetRangeInHash(hashKey, [new KeyValuePair<string, string>("_deleted", "true")]);
        transaction.Commit();

        _audit?.Log(AuditAction.RecurringStarted, target: recurringJobId);
    }

    /// <summary>
    /// Re-creates a recurring job from a stored name-based configuration (no argument values),
    /// used to restore a previously stopped job. Resolves the method by name via reflection,
    /// mirroring the legacy stop/restore behaviour.
    /// </summary>
    private void RestoreRecurringJobByName(string jobId, string typeName, string methodName, string cron, string queue, string timeZoneId)
    {
        var manager = new RecurringJobManager(_storage);
        var timeZone = string.IsNullOrEmpty(timeZoneId)
            ? TimeZoneInfo.Utc
            : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

        // Find the type and method via reflection.
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return []; } })
            .FirstOrDefault(t => t.FullName == typeName || t.Name == typeName);

        if (type is null)
            throw new ArgumentException($"Type '{typeName}' not found in loaded assemblies.");

        var method = type.GetMethod(methodName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (method is null)
            throw new ArgumentException($"Method '{methodName}' not found on type '{typeName}'.");

        var job = new global::Hangfire.Common.Job(type, method);

#pragma warning disable CS0618 // QueueName is obsolete but no alternative overload available in 1.8.x
        manager.AddOrUpdate(jobId, job, cron, new RecurringJobOptions
        {
            TimeZone = timeZone,
            QueueName = queue ?? "default"
        });
#pragma warning restore CS0618
    }

    /// <summary>
    /// Gets all stopped recurring job IDs.
    /// </summary>
    public IReadOnlyList<string> GetStoppedRecurringJobIds()
    {
        using var connection = _storage.GetReadOnlyConnection();
        if (connection is not JobStorageConnection storageConnection) return [];

        return storageConnection.GetAllItemsFromSet(StoppedJobsSet).ToList();
    }

    /// <summary>
    /// Gets the stored config for a stopped recurring job.
    /// </summary>
    public Dictionary<string, string> GetStoppedJobConfig(string recurringJobId)
    {
        using var connection = _storage.GetReadOnlyConnection();
        if (connection is not JobStorageConnection storageConnection) return null;

        var hashKey = $"recurring:stopped:{recurringJobId}";
        return storageConnection.GetAllEntriesFromHash(hashKey);
    }
}
