using Dapper;
using Npgsql;

namespace a2n.Hangfire.Dashboard.PostgreSql.Tests.Fixtures;

/// <summary>
/// Seeds 100 controlled test jobs into the Hangfire PostgreSQL schema.
/// All data is deterministic so tests can assert exact counts and values.
/// 
/// Data distribution (100 jobs):
/// 
/// States:
///   - Succeeded: 40 jobs (IDs 1-40)
///   - Failed:    20 jobs (IDs 41-60)
///   - Processing: 15 jobs (IDs 61-75)
///   - Scheduled:  10 jobs (IDs 76-85)
///   - Enqueued:   15 jobs (IDs 86-100)
/// 
/// Job Types (InvocationData):
///   - SampleJobs.SimpleJob, SampleJobs.ConsoleJob, SampleJobs.LongRunningJob, SampleJobs.TaggedJob
///   - EmailService.SendEmail, EmailService.SendBulkEmail, EmailService.SendNotification
///   - ReportGenerator.GenerateReport, ReportGenerator.GeneratePdf
///   - PaymentProcessor.ProcessPayment, PaymentProcessor.RefundPayment
///   - DataImporter.ImportCsv, DataImporter.ImportJson
///   - NotificationService.SendPush, NotificationService.SendSms
/// 
/// Queues: default, email, reports, payments, imports, notifications, critical
/// Servers: server-1:1234, server-2:5678, server-3:9012, worker-a:3000, worker-b:4000
/// Tags: email, report, critical, import, sample, payment, notification, bulk, urgent, daily
/// RecurringJobIds: simple-job, console-job, failing-job, long-running-job, report-daily, email-digest, payment-check
/// 
/// Timestamps: spread across last 7 days (168 hours)
/// Durations: 50ms to 120000ms (2 minutes)
/// </summary>
public static class TestDataSeeder
{
    // Job type definitions
    private static readonly (string Type, string Method)[] JobTypes =
    {
        ("SampleApp.Jobs.SampleJobs", "SimpleJob"),
        ("SampleApp.Jobs.SampleJobs", "ConsoleJob"),
        ("SampleApp.Jobs.SampleJobs", "LongRunningJob"),
        ("SampleApp.Jobs.SampleJobs", "TaggedJob"),
        ("SampleApp.Jobs.EmailService", "SendEmail"),
        ("SampleApp.Jobs.EmailService", "SendBulkEmail"),
        ("SampleApp.Jobs.EmailService", "SendNotification"),
        ("SampleApp.Jobs.ReportGenerator", "GenerateReport"),
        ("SampleApp.Jobs.ReportGenerator", "GeneratePdf"),
        ("SampleApp.Jobs.PaymentProcessor", "ProcessPayment"),
        ("SampleApp.Jobs.PaymentProcessor", "RefundPayment"),
        ("SampleApp.Jobs.DataImporter", "ImportCsv"),
        ("SampleApp.Jobs.DataImporter", "ImportJson"),
        ("SampleApp.Jobs.NotificationService", "SendPush"),
        ("SampleApp.Jobs.NotificationService", "SendSms"),
    };

    private static readonly string[] Queues = { "default", "email", "reports", "payments", "imports", "notifications", "critical" };
    private static readonly string[] Servers = { "server-1:1234", "server-2:5678", "server-3:9012", "worker-a:3000", "worker-b:4000" };
    private static readonly string[] Tags = { "email", "report", "critical", "import", "sample", "payment", "notification", "bulk", "urgent", "daily" };
    private static readonly string[] RecurringJobIds = { "simple-job", "console-job", "failing-job", "long-running-job", "report-daily", "email-digest", "payment-check" };

    private static readonly string[] ExceptionTypes =
    {
        "System.InvalidOperationException",
        "System.TimeoutException",
        "System.Net.Mail.SmtpException",
        "System.ArgumentNullException",
        "System.IO.IOException",
        "System.Net.Http.HttpRequestException",
        "System.Data.SqlClient.SqlException",
        "System.UnauthorizedAccessException",
        "System.OutOfMemoryException",
        "System.NullReferenceException",
    };

    private static readonly string[] ExceptionMessages =
    {
        "Operation is not valid due to the current state of the object.",
        "The operation has timed out after 30 seconds.",
        "Connection to SMTP server smtp.example.com failed: Connection refused",
        "Value cannot be null. Parameter name: userId",
        "The process cannot access the file because it is being used by another process.",
        "No connection could be made because the target machine actively refused it.",
        "Deadlock detected while executing query.",
        "Access to the path is denied.",
        "Insufficient memory to continue the execution of the program.",
        "Object reference not set to an instance of an object.",
    };

    /// <summary>
    /// Expected counts for assertions in tests.
    /// </summary>
    public static class Counts
    {
        public const int Total = 100;
        public const int Succeeded = 40;
        public const int Failed = 20;
        public const int Processing = 15;
        public const int Scheduled = 10;
        public const int Enqueued = 15;
    }

    public static async Task SeedAsync(string connectionString, string schema)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await connection.ReloadTypesAsync();

        await SeedJobsAsync(connection, schema);
        await SeedStatesAsync(connection, schema);
        await SeedTagsAsync(connection, schema);
        await SeedJobParametersAsync(connection, schema);
    }

    private static async Task SeedJobsAsync(NpgsqlConnection connection, string schema)
    {
        var jobTable = $"\"{schema}\".\"job\"";

        for (long id = 1; id <= 100; id++)
        {
            var (typeName, methodName) = JobTypes[(id - 1) % JobTypes.Length];
            var stateName = GetState(id);
            var createdAt = GetCreatedAt(id);
            var invocationData = InvocationData(typeName, methodName);
            var escapedJson = invocationData.Replace("'", "''");

            await connection.ExecuteAsync($@"
                INSERT INTO {jobTable} (id, invocationdata, arguments, createdat, statename)
                VALUES (@Id, '{escapedJson}', '[]', @CreatedAt, @StateName)",
                new { Id = id, CreatedAt = createdAt, StateName = stateName });
        }

        await connection.ExecuteAsync($"SELECT setval(pg_get_serial_sequence('{schema}.job', 'id'), 200)");
    }

    private static async Task SeedStatesAsync(NpgsqlConnection connection, string schema)
    {
        var stateTable = $"\"{schema}\".\"state\"";
        var jobTable = $"\"{schema}\".\"job\"";

        for (long id = 1; id <= 100; id++)
        {
            var stateName = GetState(id);
            var createdAt = GetCreatedAt(id).AddSeconds(1); // state created slightly after job
            var data = GetStateData(id, stateName);
            var reason = GetStateReason(id, stateName);
            var escapedData = data.Replace("'", "''");

            await connection.ExecuteAsync($@"
                INSERT INTO {stateTable} (id, jobid, name, reason, createdat, data)
                VALUES (@Id, @JobId, @Name, @Reason, @CreatedAt, '{escapedData}')",
                new { Id = id, JobId = id, Name = stateName, Reason = reason, CreatedAt = createdAt });
        }

        // Update job.stateid
        await connection.ExecuteAsync($@"
            UPDATE {jobTable} j SET stateid = s.id
            FROM {stateTable} s
            WHERE s.jobid = j.id AND s.id = j.id");

        await connection.ExecuteAsync($"SELECT setval(pg_get_serial_sequence('{schema}.state', 'id'), 200)");
    }

    private static async Task SeedTagsAsync(NpgsqlConnection connection, string schema)
    {
        var setTable = $"\"{schema}\".\"set\"";

        // Assign tags based on job characteristics
        for (long id = 1; id <= 100; id++)
        {
            var jobTags = GetTagsForJob(id);
            foreach (var tag in jobTags)
            {
                await connection.ExecuteAsync($@"
                    INSERT INTO {setTable} (key, value, score)
                    VALUES (@Key, @Value, 0.0)",
                    new { Key = $"tags:{tag}", Value = id.ToString() });
            }
        }
    }

    private static async Task SeedJobParametersAsync(NpgsqlConnection connection, string schema)
    {
        var jobParamTable = $"\"{schema}\".\"jobparameter\"";

        for (long id = 1; id <= 100; id++)
        {
            // Queue assignment
            var queue = GetQueue(id);
            await connection.ExecuteAsync($@"
                INSERT INTO {jobParamTable} (jobid, name, value)
                VALUES (@JobId, 'CurrentQueue', @Value)",
                new { JobId = id, Value = queue });

            // RecurringJobId (only for some jobs)
            var recurringId = GetRecurringJobId(id);
            if (recurringId != null)
            {
                await connection.ExecuteAsync($@"
                    INSERT INTO {jobParamTable} (jobid, name, value)
                    VALUES (@JobId, 'RecurringJobId', @Value)",
                    new { JobId = id, Value = recurringId });
            }
        }
    }

    #region Deterministic Data Generation

    /// <summary>
    /// State distribution: 1-40 Succeeded, 41-60 Failed, 61-75 Processing, 76-85 Scheduled, 86-100 Enqueued
    /// </summary>
    private static string GetState(long id) => id switch
    {
        <= 40 => "Succeeded",
        <= 60 => "Failed",
        <= 75 => "Processing",
        <= 85 => "Scheduled",
        _ => "Enqueued"
    };

    /// <summary>
    /// Timestamps spread across 7 days. Newer jobs have higher IDs.
    /// ID 1 = 168h ago, ID 100 = ~1.7h ago (linear distribution)
    /// </summary>
    private static DateTime GetCreatedAt(long id)
    {
        var hoursAgo = 168.0 - ((id - 1) * 168.0 / 99.0); // 168h to ~0h
        return DateTime.UtcNow.AddHours(-hoursAgo);
    }

    /// <summary>
    /// Queue assignment based on job type pattern.
    /// </summary>
    private static string GetQueue(long id)
    {
        var (typeName, _) = JobTypes[(id - 1) % JobTypes.Length];
        return typeName switch
        {
            "SampleApp.Jobs.EmailService" => "email",
            "SampleApp.Jobs.ReportGenerator" => "reports",
            "SampleApp.Jobs.PaymentProcessor" => "payments",
            "SampleApp.Jobs.DataImporter" => "imports",
            "SampleApp.Jobs.NotificationService" => "notifications",
            _ => id % 10 == 0 ? "critical" : "default" // every 10th SampleJobs goes to critical
        };
    }

    /// <summary>
    /// Server assignment for Processing jobs.
    /// </summary>
    private static string GetServer(long id)
    {
        return Servers[(id - 1) % Servers.Length];
    }

    /// <summary>
    /// Tags: each job gets 1-3 tags based on its characteristics.
    /// </summary>
    private static List<string> GetTagsForJob(long id)
    {
        var tags = new List<string>();
        var (typeName, _) = JobTypes[(id - 1) % JobTypes.Length];

        // Type-based tags
        if (typeName.Contains("Email")) tags.Add("email");
        if (typeName.Contains("Report")) tags.Add("report");
        if (typeName.Contains("Payment")) tags.Add("payment");
        if (typeName.Contains("Import")) tags.Add("import");
        if (typeName.Contains("Notification")) tags.Add("notification");
        if (typeName.Contains("SampleJobs")) tags.Add("sample");

        // Additional tags based on ID patterns
        if (id % 5 == 0) tags.Add("bulk");
        if (id % 7 == 0) tags.Add("urgent");
        if (id % 11 == 0) tags.Add("daily");
        if (GetQueue(id) == "critical") tags.Add("critical");

        return tags;
    }

    /// <summary>
    /// RecurringJobId: assigned to ~30% of jobs.
    /// </summary>
    private static string GetRecurringJobId(long id)
    {
        if (id % 3 != 0) return null; // only every 3rd job is recurring
        return RecurringJobIds[(id / 3 - 1) % RecurringJobIds.Length];
    }

    /// <summary>
    /// Duration for Succeeded jobs: varies from 50ms to 120000ms.
    /// Pattern: short jobs (50-500ms), medium (500-5000ms), long (5000-120000ms)
    /// </summary>
    private static double GetDuration(long id)
    {
        return (id % 10) switch
        {
            0 => 120000,  // 2 minutes
            1 => 50 + id * 3,
            2 => 100 + id * 5,
            3 => 500 + id * 10,
            4 => 1000 + id * 20,
            5 => 2000 + id * 30,
            6 => 5000 + id * 50,
            7 => 8000 + id * 80,
            8 => 15000 + id * 100,
            9 => 30000 + id * 200,
            _ => 1000
        };
    }

    private static double GetLatency(long id) => 10 + (id % 20) * 5;

    private static string GetStateData(long id, string state)
    {
        return state switch
        {
            "Succeeded" => StateData(GetDuration(id), GetLatency(id)),
            "Failed" => FailedStateData(
                ExceptionTypes[(id - 1) % ExceptionTypes.Length],
                ExceptionMessages[(id - 1) % ExceptionMessages.Length]),
            "Processing" => ProcessingStateData(GetServer(id)),
            "Scheduled" => "{}",
            "Enqueued" => $"{{\"EnqueuedAt\":\"{GetCreatedAt(id):o}\"}}",
            _ => "{}"
        };
    }

    private static string GetStateReason(long id, string state)
    {
        return state switch
        {
            "Failed" => $"Exception of type {ExceptionTypes[(id - 1) % ExceptionTypes.Length]}",
            "Scheduled" => "Scheduled for later execution",
            _ => null
        };
    }

    #endregion

    #region JSON Helper Methods

    private static string InvocationData(string typeName, string methodName)
    {
        return $"{{\"Type\":\"{typeName}, SampleApp\",\"Method\":\"{methodName}\",\"ParameterTypes\":\"[]\",\"Arguments\":\"[]\"}}";
    }

    private static string StateData(double duration, double latency)
    {
        return $"{{\"PerformanceDuration\":\"{duration}\",\"Latency\":\"{latency}\"}}";
    }

    private static string FailedStateData(string exceptionType, string exceptionMessage)
    {
        var escapedMessage = exceptionMessage.Replace("\"", "\\\"");
        return $"{{\"ExceptionType\":\"{exceptionType}\",\"ExceptionMessage\":\"{escapedMessage}\",\"ExceptionDetails\":\"at SomeMethod()\"}}";
    }

    private static string ProcessingStateData(string serverId)
    {
        return $"{{\"ServerId\":\"{serverId}\",\"StartedAt\":\"{DateTime.UtcNow:o}\"}}";
    }

    #endregion
}
