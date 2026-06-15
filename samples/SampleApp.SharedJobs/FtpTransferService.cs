using Hangfire.Console;
using Hangfire.Server;
using Hangfire.Tags.Attributes;

namespace SampleApp.SharedJobs;

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
