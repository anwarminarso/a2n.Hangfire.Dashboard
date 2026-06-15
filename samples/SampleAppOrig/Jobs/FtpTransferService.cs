using Hangfire;
using Hangfire.Common;
using Hangfire.Console;
using Hangfire.Server;
using Hangfire.States;
using Hangfire.Tags.Attributes;

namespace SampleAppOrig.Jobs;

/// <summary>
/// Issue #10 repro contract. A recurring job is built against this interface
/// (<c>RecurringJob.AddOrUpdate&lt;IFtpTransferService&gt;(...)</c>) and the concrete
/// <see cref="FtpTransferService"/> is resolved from DI at run time.
/// </summary>
/// <remarks>
/// Kept self-contained in <c>SampleAppOrig</c> (interface + implementation + filter in one file)
/// because this app targets the original <c>Hangfire.Console</c> / <c>FaceIT.Hangfire.Tags</c>
/// packages and cannot reference the shared jobs project.
/// The method mirrors the reported signature exactly: a single operator-facing parameter
/// (<c>ftpName</c>) sandwiched between the two Hangfire-injected parameters
/// (<see cref="PerformContext"/> and <see cref="System.Threading.CancellationToken"/>).
/// </remarks>
public interface IFtpTransferService
{
    [Tag("ftp")]
    [DeleteOnSuccessFilter]
    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(600)]
    [JobDisplayName("Standard FTP transfer for profile '{1}'")]
    Task StandardFileTransferServiceAsync(
        PerformContext context,
        string ftpName,
        CancellationToken cancellationToken);
}

/// <summary>
/// Concrete implementation of <see cref="IFtpTransferService"/> for issue #10. Registered in DI as
/// <c>services.AddScoped&lt;IFtpTransferService, FtpTransferService&gt;()</c> and resolved by
/// Hangfire's activator when the recurring job runs.
/// </summary>
public class FtpTransferService : IFtpTransferService
{
    [Tag("ftp")]
    public async Task StandardFileTransferServiceAsync(
        PerformContext context,
        string ftpName,
        CancellationToken cancellationToken)
    {
        context.WriteLine($"Starting file transfer for FTP profile '{ftpName}'...");

        for (var step = 1; step <= 3; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.WriteLine($"Transferring batch {step} of 3 for '{ftpName}'...");
            await Task.Delay(500, cancellationToken);
        }

        context.WriteLine($"File transfer for '{ftpName}' completed.");
    }
}

/// <summary>
/// Custom Hangfire job filter that deletes a job as soon as it succeeds instead of keeping it in
/// the Succeeded list. Named to match the <c>[DeleteOnSuccessFilter]</c> attribute described in
/// issue #10 so the sample reproduces the reported job shape end to end.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class DeleteOnSuccessFilterAttribute : JobFilterAttribute, IElectStateFilter
{
    public void OnStateElection(ElectStateContext context)
    {
        if (context.CandidateState is SucceededState)
        {
            context.CandidateState = new DeletedState
            {
                Reason = "Deleted automatically on success (DeleteOnSuccessFilter)."
            };
        }
    }
}
