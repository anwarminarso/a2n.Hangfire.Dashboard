using Hangfire.Storage.Monitoring;

namespace a2n.Hangfire.Dashboard.Interfaces;

/// <summary>
/// Reads one page of failed jobs without failing the request when a job's state
/// payload omits fields such as <c>FailedAt</c>.
/// </summary>
/// <remarks>
/// <c>Hangfire.PostgreSql</c>'s monitoring API indexes those fields on a plain
/// <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/>, so a single incomplete
/// state row throws <see cref="System.Collections.Generic.KeyNotFoundException"/> and the
/// Failed Jobs page never renders.
/// </remarks>
public interface IFailedJobPageSource
{
    JobList<FailedJobDto> GetFailedJobs(int from, int count);
}
