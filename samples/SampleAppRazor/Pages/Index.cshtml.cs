using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SampleApp.SharedJobs;

namespace SampleAppRazor.Pages;

public class IndexModel : PageModel
{
    [TempData]
    public string Message { get; set; }

    public void OnGet()
    {
    }

    public IActionResult OnPostSimpleJob()
    {
        BackgroundJob.Enqueue<SampleJobs>(x => x.SimpleJob());
        Message = "Simple Job enqueued!";
        return RedirectToPage();
    }

    public IActionResult OnPostConsoleJob()
    {
        BackgroundJob.Enqueue<SampleJobs>(x => x.ConsoleJob(null!));
        Message = "Console Job enqueued!";
        return RedirectToPage();
    }

    public IActionResult OnPostTaggedJob()
    {
        BackgroundJob.Enqueue<SampleJobs>(x => x.TaggedJob(null!));
        Message = "Tagged Job enqueued!";
        return RedirectToPage();
    }

    public IActionResult OnPostFailingJob()
    {
        BackgroundJob.Enqueue<SampleJobs>(x => x.FailingJob());
        Message = "Failing Job enqueued!";
        return RedirectToPage();
    }

    public IActionResult OnPostLongRunningJob()
    {
        BackgroundJob.Enqueue<SampleJobs>(x => x.LongRunningJob(null!));
        Message = "Long Running Job enqueued!";
        return RedirectToPage();
    }
}
