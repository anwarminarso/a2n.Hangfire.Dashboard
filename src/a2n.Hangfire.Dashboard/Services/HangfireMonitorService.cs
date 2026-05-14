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

    public JobDetailsDto? GetJobDetails(string jobId)
        => GetMonitoringApi().JobDetails(jobId);

    public long GetProcessingCount() => GetMonitoringApi().ProcessingCount();
    public long GetScheduledCount() => GetMonitoringApi().ScheduledCount();
    public long GetFailedCount() => GetMonitoringApi().FailedCount();
    public long GetSucceededListCount() => GetMonitoringApi().SucceededListCount();
    public long GetDeletedListCount() => GetMonitoringApi().DeletedListCount();
    public long GetEnqueuedCount(string queue) => GetMonitoringApi().EnqueuedCount(queue);

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
    public void CreateOrUpdateRecurringJob(string jobId, string typeName, string methodName, string cron, string? queue = null, string? timeZoneId = null)
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

        manager.AddOrUpdate(jobId, job, cron, new RecurringJobOptions
        {
            TimeZone = timeZone,
            QueueName = queue ?? "default"
        });
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
}
