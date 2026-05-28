using Hangfire;
using Hangfire.Console;
using Hangfire.Server;
using Hangfire.Tags.Attributes;

namespace SampleAppAuth.Jobs;

public class SampleJobs
{
    public void SimpleJob() => Thread.Sleep(500);

    public void ConsoleJob(PerformContext context)
    {
        context.WriteLine("Starting console job...");
        Thread.Sleep(1000);
        context.WriteLine("Console job completed.");
    }

    [Tag("orders")]
    [Tag("processing")]
    public void TaggedJob(PerformContext context)
    {
        context.WriteLine("Processing tagged job...");
        Thread.Sleep(1500);
    }

    public void FailingJob()
        => throw new InvalidOperationException("This job is designed to fail for testing purposes.");

    public void LongRunningJob(PerformContext context)
    {
        var progressBar = context.WriteProgressBar();
        for (var i = 0; i <= 100; i += 10)
        {
            progressBar.SetValue(i);
            Thread.Sleep(500);
        }
    }
}
