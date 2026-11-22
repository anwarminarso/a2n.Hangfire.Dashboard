using System;
using Bunit;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.States;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using a2n.Hangfire.Dashboard.Services;
using DetailsPage = a2n.Hangfire.Dashboard.Components.Pages.Jobs.Details;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// The Job Details banner shows the job's real expiration, read from <c>JobDetailsDto.ExpireAt</c>
/// (issue #97). It used to look for an "ExpireAt" job parameter that no storage writes, then print
/// "24 hours" for every Succeeded/Deleted job.
/// </summary>
public sealed class JobDetailsExpiryBannerTests : IDisposable
{
    private static readonly Job SampleJob = Job.FromExpression(() => Console.WriteLine("expiry"));

    // InMemory caps expirations at 3 hours by default; lift it so multi-day lifetimes are kept.
    private readonly InMemoryStorage _storage = new(new InMemoryStorageOptions { MaxExpirationTime = null });
    private readonly BackgroundJobClient _client;
    private readonly BunitContext _ctx = new();

    public JobDetailsExpiryBannerTests()
    {
        _client = new BackgroundJobClient(_storage, new JobFilterCollection());
        var options = new DashboardUIOptions();
        var monitor = new HangfireMonitorService(_storage, null, options, new JobMethodResolver());

        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddSingleton(options);
        _ctx.Services.AddDashboardPolling();
        _ctx.Services.AddSingleton(monitor);
        _ctx.Services.AddSingleton(new ConsoleDataReader(_storage));
        _ctx.Services.AddSingleton(new TagsDataReader(_storage));
        _ctx.Services.AddSingleton(new JobGraphService(monitor));
    }

    public void Dispose() => _ctx.Dispose();

    private void ExpireJob(string jobId, TimeSpan expireIn)
    {
        using var connection = _storage.GetConnection();
        using var tx = connection.CreateWriteTransaction();
        tx.ExpireJob(jobId, expireIn);
        tx.Commit();
    }

    private void PersistJob(string jobId)
    {
        using var connection = _storage.GetConnection();
        using var tx = connection.CreateWriteTransaction();
        tx.PersistJob(jobId);
        tx.Commit();
    }

    private IRenderedComponent<DetailsPage> Render(string jobId)
    {
        var cut = _ctx.Render<DetailsPage>(p => p.Add(c => c.JobId, jobId));
        cut.WaitForState(() => cut.Markup.Contains("State History"), TestTimeouts.RenderWait);
        return cut;
    }

    private static string Banner(IRenderedComponent<DetailsPage> cut)
        => cut.Find("div.alert[role=alert]").TextContent;

    [Theory]
    [InlineData("Succeeded", 120, "It will be removed automatically in 5 days.")]
    [InlineData("Deleted", 120, "It will be removed automatically in 5 days.")]
    [InlineData("Succeeded", 36, "It will be removed automatically in 36 hours.")]
    [InlineData("Succeeded", 24, "It will be removed automatically in 24 hours.")]
    [InlineData("Succeeded", 1, "It will be removed automatically in 1 hour.")]
    public void Banner_ShowsTheStoredExpiration(string state, int hours, string expected)
    {
        IState jobState = state == "Deleted" ? new DeletedState() : new SucceededState(null, 0, 0);
        var id = _client.Create(SampleJob, jobState);
        ExpireJob(id, TimeSpan.FromHours(hours));

        var banner = Banner(Render(id));

        Assert.StartsWith($"The job is {state.ToLowerInvariant()}.", banner.Trim());
        Assert.EndsWith(expected, banner.Trim());
    }

    [Fact]
    public void Banner_TooltipHasTheExactExpirationInUtc()
    {
        var id = _client.Create(SampleJob, new SucceededState(null, 0, 0));
        ExpireJob(id, TimeSpan.FromDays(5));

        var cut = Render(id);
        var expireAt = _storage.GetMonitoringApi().JobDetails(id).ExpireAt;

        Assert.NotNull(expireAt);
        var title = cut.Find("div.alert[role=alert] span[title]").GetAttribute("title");
        Assert.Equal($"{expireAt.Value:yyyy-MM-dd HH:mm:ss} UTC", title);
    }

    [Fact]
    public void NoBanner_WhenTheJobHasNoExpiration()
    {
        var id = _client.Create(SampleJob, new SucceededState(null, 0, 0));
        PersistJob(id);

        var cut = Render(id);

        Assert.DoesNotContain("removed automatically", cut.Markup);
    }

    [Fact]
    public void NoBanner_ForAFailedJob()
    {
        var id = _client.Create(SampleJob, new FailedState(new InvalidOperationException("boom")));

        var cut = Render(id);

        Assert.DoesNotContain("removed automatically", cut.Markup);
    }
}
