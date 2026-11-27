using System;

namespace a2n.Hangfire.Dashboard.Heatmap;

/// <summary>
/// The execution class an historical job execution belongs to, as displayed by the heatmap.
/// </summary>
public enum ExecutionClass
{
    /// <summary>
    /// A recurring (cron) execution that carries a <c>RecurringJobId</c>. Such executions are
    /// projectable and reschedulable.
    /// </summary>
    Cron,

    /// <summary>
    /// An on-demand execution that carries no <c>RecurringJobId</c> (e.g. fire-and-forget, scheduled
    /// one-off, continuation, batch work, or a manual "Trigger now" that was not initiated from a
    /// recurring job). Such executions are demand-driven and cannot be projected or rescheduled.
    /// </summary>
    AdHoc
}

/// <summary>
/// Pure, deterministic classification of historical job executions into a <see cref="ExecutionClass"/>.
/// </summary>
/// <remarks>
/// <para>The classification rule is solely the presence of a <c>RecurringJobId</c>: an execution is a
/// <see cref="ExecutionClass.Cron"/> execution when it carries a non-empty <c>RecurringJobId</c> and an
/// <see cref="ExecutionClass.AdHoc"/> execution otherwise. A manual "Trigger now" of a recurring job is
/// therefore classified as <see cref="ExecutionClass.Cron"/> when it carries a <c>RecurringJobId</c>,
/// and as <see cref="ExecutionClass.AdHoc"/> when it does not.</para>
/// <para>Validates Requirements 16.1 and 24.1.</para>
/// </remarks>
public static class ExecutionClassifier
{
    /// <summary>
    /// Classifies a historical job execution by the presence of its <c>RecurringJobId</c>.
    /// </summary>
    /// <param name="recurringJobId">
    /// The execution's <c>RecurringJobId</c>, or <see langword="null"/>/whitespace when the execution
    /// did not originate from a recurring job.
    /// </param>
    /// <returns>
    /// <see cref="ExecutionClass.Cron"/> when <paramref name="recurringJobId"/> is non-null and not
    /// whitespace; otherwise <see cref="ExecutionClass.AdHoc"/> (Req 16.1, 24.1).
    /// </returns>
    public static ExecutionClass Classify(string recurringJobId)
        => string.IsNullOrWhiteSpace(recurringJobId)
            ? ExecutionClass.AdHoc
            : ExecutionClass.Cron;
}
