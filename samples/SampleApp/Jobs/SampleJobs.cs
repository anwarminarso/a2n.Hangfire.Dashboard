using Hangfire;
using Hangfire.Console;
using Hangfire.Server;
using Hangfire.Tags.Attributes;

namespace SampleApp.Jobs;

public class SampleJobs
{
    /// <summary>
    /// A simple job that does nothing special.
    /// </summary>
    public void SimpleJob()
    {
        Thread.Sleep(500);
    }

    /// <summary>
    /// A job that writes console output — demonstrates Hangfire.Console integration.
    /// </summary>
    public void ConsoleJob(PerformContext context)
    {
        context.WriteLine("Starting console job...");
        context.WriteLine("Processing step 1 of 3");
        Thread.Sleep(1000);

        context.WriteLine("Processing step 2 of 3");
        Thread.Sleep(1000);

        context.WriteLine("Processing step 3 of 3");
        Thread.Sleep(500);

        context.WriteLine("Console job completed successfully.");
    }

    /// <summary>
    /// A job with tags — demonstrates Hangfire.Tags integration.
    /// </summary>
    [Tag("orders")]
    [Tag("processing")]
    public void TaggedJob(PerformContext context)
    {
        context.WriteLine("Processing tagged job...");
        Thread.Sleep(1500);
        context.WriteLine("Tagged job done.");
    }

    /// <summary>
    /// A job that always fails — useful for testing failed jobs view.
    /// </summary>
    public void FailingJob()
    {
        throw new InvalidOperationException("This job is designed to fail for testing purposes.");
    }

    /// <summary>
    /// A long-running job with progress bar — demonstrates progress tracking.
    /// </summary>
    public void LongRunningJob(PerformContext context)
    {
        var progressBar = context.WriteProgressBar();

        for (var i = 0; i <= 100; i += 10)
        {
            context.WriteLine($"Progress: {i}%");
            progressBar.SetValue(i);
            Thread.Sleep(500);
        }

        context.WriteLine("Long running job completed.");
    }



    /// <summary>
    /// A long-running job with progress bar — demonstrates progress tracking.
    /// </summary>
    [JobDisplayName("Long Running Job with Custom Label")]
    public void LongRunningJobLabel(PerformContext context)
    {
        var progressBar = context.WriteProgressBar();

        for (var i = 0; i <= 100; i += 10)
        {
            context.WriteLine($"Progress: {i}%");
            progressBar.SetValue(i);
            Thread.Sleep(500);
        }

        context.WriteLine("Long running job with custom label completed.");
    }

}
