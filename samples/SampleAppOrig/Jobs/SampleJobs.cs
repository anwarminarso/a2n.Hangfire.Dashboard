using Hangfire;
using Hangfire.Console;
using Hangfire.Server;
using Hangfire.Tags.Attributes;

namespace SampleAppOrig.Jobs;

/// <summary>
/// Self-contained job catalog for <c>SampleAppOrig</c>. Mirrors <c>SampleApp.SharedJobs.SampleJobs</c>
/// but is kept as a single local file because this app targets the original
/// <c>Hangfire.Console</c> / <c>FaceIT.Hangfire.Tags</c> packages (different dependencies),
/// so it cannot reference the shared jobs project.
/// Exposes representative jobs for every dashboard scenario: simple, console, tags, failures,
/// progress, and continuation pipelines.
/// </summary>
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

    /// <summary>
    /// Same as <see cref="LongRunningJob"/> but with a custom <see cref="JobDisplayNameAttribute"/> label.
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

    [JobDisplayName("Pipeline · Step 1 (Extract)")]
    public void PipelineExtract(PerformContext context)
    {
        context.WriteLine("Extracting data...");
        Thread.Sleep(800);
    }

    [JobDisplayName("Pipeline · Step 2 (Transform)")]
    public void PipelineTransform(PerformContext context)
    {
        context.WriteLine("Transforming data...");
        Thread.Sleep(800);
    }

    [JobDisplayName("Pipeline · Step 3 (Load)")]
    public void PipelineLoad(PerformContext context)
    {
        context.WriteLine("Loading data...");
        Thread.Sleep(800);
    }

    [JobDisplayName("Pipeline · Notify on success")]
    public void PipelineNotifySuccess(PerformContext context)
    {
        context.WriteLine("Pipeline succeeded — sending success notification.");
        Thread.Sleep(300);
    }

    [JobDisplayName("Pipeline · Notify on failure")]
    public void PipelineNotifyFailure(PerformContext context)
    {
        context.WriteLine("Pipeline failed — sending failure notification.");
        Thread.Sleep(300);
    }

    /// <summary>
    /// Seeds a continuation pipeline (Extract → Transform → Load → Notify on success/failure).
    /// Triggered by a recurring job to keep the dependency graph viewer populated with fresh demo data.
    /// Uses <see cref="PerformContext"/> so this seeder job itself becomes the root parent of the pipeline,
    /// producing a complete graph visible from every node (including the trigger).
    /// </summary>
    public void SeedPipeline(PerformContext context)
    {
        var rootId = context.BackgroundJob.Id;
        var extractId = BackgroundJob.ContinueJobWith<SampleJobs>(rootId, j => j.PipelineExtract(null!));
        var transformId = BackgroundJob.ContinueJobWith<SampleJobs>(extractId, j => j.PipelineTransform(null!));
        var loadId = BackgroundJob.ContinueJobWith<SampleJobs>(transformId, j => j.PipelineLoad(null!));
        BackgroundJob.ContinueJobWith<SampleJobs>(
            loadId,
            j => j.PipelineNotifySuccess(null!),
            JobContinuationOptions.OnlyOnSucceededState);
        BackgroundJob.ContinueJobWith<SampleJobs>(
            loadId,
            j => j.PipelineNotifyFailure(null!),
            JobContinuationOptions.OnlyOnDeletedState);
    }
}
