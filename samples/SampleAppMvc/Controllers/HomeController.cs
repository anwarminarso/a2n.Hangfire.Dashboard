using Hangfire;
using Microsoft.AspNetCore.Mvc;
using SampleApp.SharedJobs;

namespace SampleAppMvc.Controllers;

public class HomeController : Controller
{
    public IActionResult Index()
    {
        return View();
    }

    [HttpPost]
    public IActionResult EnqueueSimpleJob()
    {
        BackgroundJob.Enqueue<SampleJobs>(x => x.SimpleJob());
        TempData["Message"] = "Simple Job enqueued!";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public IActionResult EnqueueConsoleJob()
    {
        BackgroundJob.Enqueue<SampleJobs>(x => x.ConsoleJob(null!));
        TempData["Message"] = "Console Job enqueued!";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public IActionResult EnqueueTaggedJob()
    {
        BackgroundJob.Enqueue<SampleJobs>(x => x.TaggedJob(null!));
        TempData["Message"] = "Tagged Job enqueued!";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public IActionResult EnqueueFailingJob()
    {
        BackgroundJob.Enqueue<SampleJobs>(x => x.FailingJob());
        TempData["Message"] = "Failing Job enqueued!";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public IActionResult EnqueueLongRunningJob()
    {
        BackgroundJob.Enqueue<SampleJobs>(x => x.LongRunningJob(null!));
        TempData["Message"] = "Long Running Job enqueued!";
        return RedirectToAction(nameof(Index));
    }
}
