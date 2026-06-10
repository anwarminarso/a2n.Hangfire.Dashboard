using Dapper;
using Microsoft.Data.SqlClient;

namespace a2n.Hangfire.Dashboard.SqlServer.Tests.Fixtures;

/// <summary>
/// Seeds 100 controlled test jobs into the Hangfire SQL Server schema.
/// Mirrors the PostgreSQL test seeder so assertions stay identical across providers.
/// All data is deterministic so tests can assert exact counts and values.
///
/// Data distribution (100 jobs):
///   - Succeeded: 40 jobs (IDs 1-40)
///   - Failed:    20 jobs (IDs 41-60)
///   - Processing: 15 jobs (IDs 61-75)
///   - Scheduled:  10 jobs (IDs 76-85)
///   - Enqueued:   15 jobs (IDs 86-100)
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

    private static readonly string[] Servers = { "server-1:1234", "server-2:5678", "server-3:9012", "worker-a:3000", "worker-b:4000" };

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
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await SeedJobsAndStatesAsync(connection, schema);
        await SeedTagsAsync(connection, schema);
        await SeedJobParametersAsync(connection, schema);
    }

    private static async Task SeedJobsAndStatesAsync(SqlConnection connection, string schema)
    {
        var jobTable = $"[{schema}].[Job]";
        var stateTable = $"[{schema}].[State]";

        // Job + State both carry explicit IDs (1..100) so the two line up 1:1 and
        // Job.StateId can be wired deterministically. Both columns are IDENTITY in the
        // Hangfire schema, so IDENTITY_INSERT must be toggled per table.
        await connection.ExecuteAsync($"SET IDENTITY_INSERT {jobTable} ON");
        for (long id = 1; id <= 100; id++)
        {
            var (typeName, methodName) = JobTypes[(id - 1) % JobTypes.Length];
            var stateName = GetState(id);
            var createdAt = GetCreatedAt(id);
            var invocationData = InvocationData(typeName, methodName);

            await connection.ExecuteAsync($@"
                INSERT INTO {jobTable} (Id, InvocationData, Arguments, CreatedAt, StateName)
                VALUES (@Id, @InvocationData, '[]', @CreatedAt, @StateName)",
                new { Id = id, InvocationData = invocationData, CreatedAt = createdAt, StateName = stateName });
        }
        await connection.ExecuteAsync($"SET IDENTITY_INSERT {jobTable} OFF");

        await connection.ExecuteAsync($"SET IDENTITY_INSERT {stateTable} ON");
        for (long id = 1; id <= 100; id++)
        {
            var stateName = GetState(id);
            var createdAt = GetCreatedAt(id).AddSeconds(1); // state created slightly after job
            var data = GetStateData(id, stateName);
            var reason = GetStateReason(id, stateName);

            await connection.ExecuteAsync($@"
                INSERT INTO {stateTable} (Id, JobId, Name, Reason, CreatedAt, Data)
                VALUES (@Id, @JobId, @Name, @Reason, @CreatedAt, @Data)",
                new { Id = id, JobId = id, Name = stateName, Reason = reason, CreatedAt = createdAt, Data = data });
        }
        await connection.ExecuteAsync($"SET IDENTITY_INSERT {stateTable} OFF");

        // Point each job at its (matching-id) state row.
        await connection.ExecuteAsync($@"
            UPDATE j SET StateId = s.Id
            FROM {jobTable} j
            INNER JOIN {stateTable} s ON s.JobId = j.Id AND s.Id = j.Id");
    }

    private static async Task SeedTagsAsync(SqlConnection connection, string schema)
    {
        var setTable = $"[{schema}].[Set]";

        for (long id = 1; id <= 100; id++)
        {
            foreach (var tag in GetTagsForJob(id))
            {
                await connection.ExecuteAsync($@"
                    INSERT INTO {setTable} ([Key], Value, Score)
                    VALUES (@Key, @Value, 0.0)",
                    new { Key = $"tags:{tag}", Value = id.ToString() });
            }
        }
    }

    private static async Task SeedJobParametersAsync(SqlConnection connection, string schema)
    {
        var jobParamTable = $"[{schema}].[JobParameter]";

        for (long id = 1; id <= 100; id++)
        {
            var queue = GetQueue(id);
            await connection.ExecuteAsync($@"
                INSERT INTO {jobParamTable} (JobId, Name, Value)
                VALUES (@JobId, 'Job.Queue', @Value)",
                new { JobId = id, Value = queue });

            var recurringId = GetRecurringJobId(id);
            if (recurringId != null)
            {
                await connection.ExecuteAsync($@"
                    INSERT INTO {jobParamTable} (JobId, Name, Value)
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
    /// ID 1 = 168h ago, ID 100 = ~0h ago (linear distribution)
    /// </summary>
    private static DateTime GetCreatedAt(long id)
    {
        var hoursAgo = 168.0 - ((id - 1) * 168.0 / 99.0);
        return DateTime.UtcNow.AddHours(-hoursAgo);
    }

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
            _ => id % 10 == 0 ? "critical" : "default"
        };
    }

    private static string GetServer(long id) => Servers[(id - 1) % Servers.Length];

    private static List<string> GetTagsForJob(long id)
    {
        var tags = new List<string>();
        var (typeName, _) = JobTypes[(id - 1) % JobTypes.Length];

        if (typeName.Contains("Email")) tags.Add("email");
        if (typeName.Contains("Report")) tags.Add("report");
        if (typeName.Contains("Payment")) tags.Add("payment");
        if (typeName.Contains("Import")) tags.Add("import");
        if (typeName.Contains("Notification")) tags.Add("notification");
        if (typeName.Contains("SampleJobs")) tags.Add("sample");

        if (id % 5 == 0) tags.Add("bulk");
        if (id % 7 == 0) tags.Add("urgent");
        if (id % 11 == 0) tags.Add("daily");
        if (GetQueue(id) == "critical") tags.Add("critical");

        return tags;
    }

    private static string GetRecurringJobId(long id)
    {
        if (id % 3 != 0) return null;
        var recurringJobIds = new[] { "simple-job", "console-job", "failing-job", "long-running-job", "report-daily", "email-digest", "payment-check" };
        return recurringJobIds[(id / 3 - 1) % recurringJobIds.Length];
    }

    private static double GetDuration(long id) => (id % 10) switch
    {
        0 => 120000,
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

    private static double GetLatency(long id) => 10 + (id % 20) * 5;

    private static string GetStateData(long id, string state) => state switch
    {
        "Succeeded" => StateData(GetDuration(id), GetLatency(id)),
        "Failed" => FailedStateData(
            ExceptionTypes[(id - 1) % ExceptionTypes.Length],
            ExceptionMessages[(id - 1) % ExceptionMessages.Length]),
        "Processing" => ProcessingStateData(GetServer(id), GetQueue(id)),
        "Scheduled" => "{}",
        "Enqueued" => $"{{\"Queue\":\"{GetQueue(id)}\",\"EnqueuedAt\":\"{GetCreatedAt(id):o}\"}}",
        _ => "{}"
    };

    private static string GetStateReason(long id, string state) => state switch
    {
        "Failed" => $"Exception of type {ExceptionTypes[(id - 1) % ExceptionTypes.Length]}",
        "Scheduled" => "Scheduled for later execution",
        _ => null
    };

    #endregion

    #region JSON Helper Methods

    private static string InvocationData(string typeName, string methodName)
        => $"{{\"Type\":\"{typeName}, SampleApp\",\"Method\":\"{methodName}\",\"ParameterTypes\":\"[]\",\"Arguments\":\"[]\"}}";

    private static string StateData(double duration, double latency)
        => $"{{\"PerformanceDuration\":\"{duration}\",\"Latency\":\"{latency}\"}}";

    private static string FailedStateData(string exceptionType, string exceptionMessage)
    {
        var escapedMessage = exceptionMessage.Replace("\"", "\\\"");
        return $"{{\"ExceptionType\":\"{exceptionType}\",\"ExceptionMessage\":\"{escapedMessage}\",\"ExceptionDetails\":\"at SomeMethod()\"}}";
    }

    private static string ProcessingStateData(string serverId, string queue)
        => $"{{\"ServerId\":\"{serverId}\",\"Queue\":\"{queue}\",\"StartedAt\":\"{DateTime.UtcNow:o}\"}}";

    #endregion
}
