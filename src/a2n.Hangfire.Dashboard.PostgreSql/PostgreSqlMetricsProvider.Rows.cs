
namespace a2n.Hangfire.Dashboard.PostgreSql;

public partial class PostgreSqlMetricsProvider
{
    #region Internal Row Types

    private class CounterRow
    {
        public string? Key { get; set; }
        public decimal Value { get; set; }
    }

    private class StateTransitionRow
    {
        public DateTime Bucket { get; set; }
        public string? StateName { get; set; }
        public long Count { get; set; }
    }

    private class ServerRow
    {
        public string? ServerName { get; set; }
        public string? Data { get; set; }
        public DateTime LastHeartbeat { get; set; }
    }

    private class BusyWorkerRow
    {
        public string? ServerName { get; set; }
        public int BusyCount { get; set; }
    }

    private class QueueThroughputRow
    {
        public DateTime Bucket { get; set; }
        public string? QueueName { get; set; }
        public long SucceededCount { get; set; }
    }

    private class HashRow
    {
        public string Key { get; set; } = null!;
        public string Field { get; set; } = null!;
        public string? Value { get; set; }
    }

    private class RecurringExecutionSummaryRow
    {
        public string? RecurringJobIdStored { get; set; }
        public string? StateName { get; set; }
        public decimal DurationMs { get; set; }
    }

    private class RecurringExecutionBatchRow
    {
        public string? RecurringJobIdStored { get; set; }
        public string? JobId { get; set; }
        public DateTimeOffset ExecutedAt { get; set; }
        public double DurationMs { get; set; }
        public bool Succeeded { get; set; }
        public string? ErrorMessage { get; set; }
    }

    #endregion
}
