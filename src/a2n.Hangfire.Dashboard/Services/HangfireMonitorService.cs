using Hangfire;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Service that wraps Hangfire's IMonitoringApi for dashboard data access.
/// </summary>
public class HangfireMonitorService
{
    private readonly JobStorage _storage;

    public HangfireMonitorService(JobStorage storage)
    {
        _storage = storage;
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
        return client.ChangeState(jobId, new EnqueuedState());
    }

    /// <summary>
    /// Delete a job (change state to Deleted).
    /// </summary>
    public bool DeleteJob(string jobId)
    {
        var client = new BackgroundJobClient(_storage);
        return client.ChangeState(jobId, new DeletedState { Reason = "Deleted via Dashboard" });
    }

    /// <summary>
    /// Requeue multiple jobs.
    /// </summary>
    public int RequeueJobs(IEnumerable<string> jobIds)
    {
        var client = new BackgroundJobClient(_storage);
        var count = 0;
        foreach (var jobId in jobIds)
        {
            if (client.ChangeState(jobId, new EnqueuedState()))
                count++;
        }
        return count;
    }

    /// <summary>
    /// Delete multiple jobs.
    /// </summary>
    public int DeleteJobs(IEnumerable<string> jobIds)
    {
        var client = new BackgroundJobClient(_storage);
        var count = 0;
        foreach (var jobId in jobIds)
        {
            if (client.ChangeState(jobId, new DeletedState { Reason = "Deleted via Dashboard" }))
                count++;
        }
        return count;
    }

    /// <summary>
    /// Create or update a recurring job.
    /// </summary>
    public void CreateOrUpdateRecurringJob(string jobId, string typeName, string methodName, string cron, string queue = null, string timeZoneId = null)
    {
        var manager = new RecurringJobManager(_storage);
        var timeZone = string.IsNullOrEmpty(timeZoneId)
            ? TimeZoneInfo.Utc
            : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

        // Find the type and method via reflection
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
    /// Trigger a recurring job immediately.
    /// </summary>
    public void TriggerRecurringJob(string recurringJobId)
    {
        var manager = new RecurringJobManager(_storage);
        manager.Trigger(recurringJobId);
    }

    /// <summary>
    /// Remove a recurring job.
    /// </summary>
    public void RemoveRecurringJob(string recurringJobId)
    {
        var manager = new RecurringJobManager(_storage);
        manager.RemoveIfExists(recurringJobId);
    }

    /// <summary>
    /// Trigger multiple recurring jobs.
    /// </summary>
    public void TriggerRecurringJobs(IEnumerable<string> recurringJobIds)
    {
        var manager = new RecurringJobManager(_storage);
        foreach (var id in recurringJobIds)
            manager.Trigger(id);
    }

    /// <summary>
    /// Remove multiple recurring jobs.
    /// </summary>
    public void RemoveRecurringJobs(IEnumerable<string> recurringJobIds)
    {
        var manager = new RecurringJobManager(_storage);
        foreach (var id in recurringJobIds)
            manager.RemoveIfExists(id);
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

        // Re-create the recurring job
        CreateOrUpdateRecurringJob(recurringJobId, typeName, methodName, cron, queue, timeZoneId);

        // Clean up the stopped state
        using var transaction = connection.CreateWriteTransaction();
        transaction.RemoveFromSet(StoppedJobsSet, recurringJobId);
        // Mark the hash as deleted by overwriting with a flag
        transaction.SetRangeInHash(hashKey, [new KeyValuePair<string, string>("_deleted", "true")]);
        transaction.Commit();
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
