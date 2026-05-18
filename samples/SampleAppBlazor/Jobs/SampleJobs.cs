using Hangfire;
using Hangfire.Console;
using Hangfire.Server;
using Hangfire.Tags.Attributes;

namespace SampleAppBlazor.Jobs;

public class SampleJobs
{
    public void SimpleJob()
    {
        Thread.Sleep(500);
    }

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

    [Tag("orders")]
    [Tag("processing")]
    public void TaggedJob(PerformContext context)
    {
        context.WriteLine("Processing tagged job...");
        Thread.Sleep(1500);
        context.WriteLine("Tagged job done.");
    }

    public void FailingJob()
    {
        throw new InvalidOperationException("This job is designed to fail for testing purposes.");
    }

    [JobDisplayName("Long Running Job")]
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
}
