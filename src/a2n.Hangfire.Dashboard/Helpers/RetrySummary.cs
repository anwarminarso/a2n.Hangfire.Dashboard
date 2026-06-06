using Hangfire.Storage.Monitoring;

namespace a2n.Hangfire.Dashboard.Helpers;

/// <summary>
/// Aggregates retry information from a job's state history into a small summary
/// the UI can render as a single-line banner under the "State History" header.
/// </summary>
public class RetrySummary
{
    /// <summary>Number of failed Processing attempts (i.e. distinct retries before the final state).</summary>
    public int RetryCount { get; init; }

    /// <summary>True when every failure used the same exception type (often signals a persistent root cause).</summary>
    public bool AllSameException { get; init; }

    /// <summary>Distinct exception types encountered across all failed attempts, ordered by first occurrence.</summary>
    public IReadOnlyList<string> DistinctExceptionTypes { get; init; } = Array.Empty<string>();

    /// <summary>Final state name observed (e.g. <c>"Succeeded"</c>, <c>"Failed"</c>, <c>"Deleted"</c>).</summary>
    public string FinalState { get; init; }

    /// <summary>True when the banner should render (i.e. there is at least one retry).</summary>
    public bool ShouldDisplay => RetryCount > 0;

    /// <summary>
    /// Builds a summary from a <see cref="JobDetailsDto"/> history.
    /// Returns an instance with <see cref="RetryCount"/> = 0 (and <see cref="ShouldDisplay"/> = false)
    /// when the history reflects a job that never retried.
    /// </summary>
    public static RetrySummary FromHistory(JobDetailsDto job)
    {
        if (job?.History is null || job.History.Count == 0)
        {
            return new RetrySummary();
        }

        // History is newest-first. Walk in chronological order so attempt numbering matches user expectation.
        var chronological = job.History.Reverse().ToList();

        var failedExceptions = new List<string>();
        foreach (var entry in chronological)
        {
            if (!string.Equals(entry.StateName, "Failed", StringComparison.Ordinal)) continue;
            if (entry.Data is null) continue;
            entry.Data.TryGetValue("ExceptionType", out var exType);
            failedExceptions.Add(string.IsNullOrEmpty(exType) ? "(unknown)" : exType);
        }

        // Retries = total failed attempts minus 1 IF the job ultimately stays Failed (the last Failed isn't a retry — it's terminal).
        // If the job ended Succeeded, every Failed represents a retry that triggered the next attempt.
        var finalState = job.History[0].StateName;
        var retryCount = string.Equals(finalState, "Failed", StringComparison.Ordinal)
            ? Math.Max(0, failedExceptions.Count - 1)
            : failedExceptions.Count;

        var distinct = failedExceptions
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new RetrySummary
        {
            RetryCount = retryCount,
            AllSameException = distinct.Count <= 1 && failedExceptions.Count >= 2,
            DistinctExceptionTypes = distinct,
            FinalState = finalState,
        };
    }

    /// <summary>
    /// Returns a short human-readable phrase describing exception consistency.
    /// </summary>
    public string GetExceptionPhrase()
    {
        if (DistinctExceptionTypes.Count == 0) return string.Empty;
        if (DistinctExceptionTypes.Count == 1)
        {
            return RetryCount > 0
                ? $"same exception each time ({ShortType(DistinctExceptionTypes[0])})"
                : ShortType(DistinctExceptionTypes[0]);
        }
        return $"{DistinctExceptionTypes.Count} different exceptions";
    }

    /// <summary>
    /// Returns a tooltip listing the distinct exception types, suitable for a hover hint on the banner badge.
    /// </summary>
    public string GetExceptionTooltip()
    {
        if (DistinctExceptionTypes.Count == 0) return null;
        return string.Join(" · ", DistinctExceptionTypes.Select(ShortType));
    }

    private static string ShortType(string fullType)
    {
        if (string.IsNullOrEmpty(fullType)) return fullType;
        var lastDot = fullType.LastIndexOf('.');
        return lastDot >= 0 && lastDot < fullType.Length - 1 ? fullType[(lastDot + 1)..] : fullType;
    }
}
