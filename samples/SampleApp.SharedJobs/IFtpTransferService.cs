using Hangfire;
using Hangfire.Server;
using Hangfire.Tags.Attributes;

namespace SampleApp.SharedJobs;

/// <summary>
/// Issue #10 repro contract. A recurring job is built against this interface
/// (<c>RecurringJob.AddOrUpdate&lt;IFtpTransferService&gt;(...)</c>) and the concrete
/// <see cref="FtpTransferService"/> is resolved from DI at run time.
/// </summary>
/// <remarks>
/// The method mirrors the reported signature exactly: a single operator-facing parameter
/// (<c>ftpName</c>) sandwiched between the two Hangfire-injected parameters
/// (<see cref="PerformContext"/> and <see cref="System.Threading.CancellationToken"/>). The job
/// filters declared here — <c>[Tag]</c>, <c>[DeleteOnSuccessFilter]</c>, <c>[AutomaticRetry]</c>,
/// and <c>[DisableConcurrentExecution]</c> — are placed on the interface so they apply regardless
/// of how the job is created.
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
