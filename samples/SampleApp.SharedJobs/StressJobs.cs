using Hangfire;

namespace SampleApp.SharedJobs;

/// <summary>
/// Jobs the load-test harness (<c>tests/load</c>) enqueues in bulk. They do no work, so a worker runs
/// thousands of them a second, and <see cref="Fail"/> throws one of <see cref="FailureKinds"/> kinds
/// of exception, so the Failed page gets many groups whose stored fingerprints come from the real
/// <c>FailureFingerprintFilter</c> rather than from a copy of its rules.
/// </summary>
public class StressJobs
{
    private static readonly string[] Subjects =
    {
        "invoice", "order", "payment", "shipment", "customer", "report", "export", "import", "webhook", "mailbox",
    };

    private static readonly string[] Problems =
    {
        "was rejected by the gateway", "is locked by another process", "could not be found",
        "exceeded its quota", "failed validation",
    };

    /// <summary>How many distinct kinds of failure <see cref="Fail"/> produces.</summary>
    public const int FailureKinds = 50;

    /// <summary>Completes at once.</summary>
    public void Succeed(int batch)
    {
    }

    /// <summary>
    /// Throws failure <paramref name="kind"/> (modulo <see cref="FailureKinds"/>). Each kind has its
    /// own exception type or message, so each fingerprints differently; every fifth one wraps an
    /// inner exception, as EF Core's <c>DbUpdateException</c> does. Not retried, so the job is
    /// Failed after one attempt.
    /// </summary>
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public void Fail(int kind)
    {
        kind = ((kind % FailureKinds) + FailureKinds) % FailureKinds;
        var message = $"The {Subjects[kind % Subjects.Length]} {Problems[kind / Subjects.Length]}.";

        Exception failure = (kind % 3) switch
        {
            0 => new InvalidOperationException(message),
            1 => new TimeoutException(message),
            _ => new ArgumentException(message),
        };

        if (kind % 5 == 4)
            failure = new ApplicationException("Saving the changes failed.", failure);

        throw failure;
    }
}
