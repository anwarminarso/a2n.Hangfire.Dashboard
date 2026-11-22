using System;
using System.Linq;
using Bunit;
using Hangfire.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using a2n.Hangfire.Dashboard.Services;
using RecurringPage = a2n.Hangfire.Dashboard.Components.Pages.Recurring;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// The Recurring page lists every job in the stopped set (issue #98). Before, it skipped an entry
/// whose hash had a "_deleted" field, which a Stop after a Start kept, so the job was in neither list.
/// </summary>
public sealed class RecurringStoppedSectionTests : IDisposable
{
    private readonly InMemoryStorage _storage = new();
    private readonly HangfireMonitorService _monitor;
    private readonly BunitContext _ctx = new();

    public RecurringStoppedSectionTests()
    {
        var options = new DashboardUIOptions { EnableJobManagement = true };
        _monitor = new HangfireMonitorService(_storage, null, options, new JobMethodResolver());

        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddSingleton(options);
        _ctx.Services.AddDashboardPolling();
        _ctx.Services.AddSingleton(_monitor);
        _ctx.Services.AddSingleton(new ConsoleDataReader(_storage));
        _ctx.Services.AddSingleton(new TagsDataReader(_storage));
        _ctx.Services.AddSingleton(new JobGraphService(_monitor));
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void AJobAnEarlierVersionHid_IsListedAsStopped()
    {
        RecurringStopStartTests.SeedHiddenStoppedJob(_storage, "hidden");

        var cut = _ctx.Render<RecurringPage>();
        cut.WaitForState(() => cut.Markup.Contains("Stopped Jobs"), TestTimeouts.RenderWait);

        var ids = cut.FindAll("tr.table-secondary td.hf-cell-title").Select(td => td.TextContent.Trim());
        Assert.Contains("hidden", ids);
    }
}
