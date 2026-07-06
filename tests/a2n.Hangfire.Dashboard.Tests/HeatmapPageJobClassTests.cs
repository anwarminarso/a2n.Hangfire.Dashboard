using System.Collections.Generic;
using System.Linq;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Interfaces;
using AngleSharp.Dom;
using Bunit;
using Hangfire;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using HeatmapPage = a2n.Hangfire.Dashboard.Components.Pages.Heatmap.Heatmap;
using HeatmapService = a2n.Hangfire.Dashboard.Services.HeatmapService;
using HangfireMonitorService = a2n.Hangfire.Dashboard.Services.HangfireMonitorService;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// bUnit tests for the Heatmap page's Job_Class selector and ad-hoc/combined gating (task 15.4).
///
/// These assert the requirement-level behavior wired into <c>Heatmap.razor</c>:
///   • Req 16.2 — the selector offers exactly Cron / Ad-hoc / Combined, with Combined the default
///     when a metrics provider is registered;
///   • Req 16.6 — the Ad-hoc class forces the Historical source;
///   • Req 16.7 — without a provider, Ad-hoc / Combined are disabled and the page defaults to Cron
///     on the Projected source (the source toggle is hidden);
///   • Req 16.9 — with a provider, Ad-hoc and Combined are enabled together as a pair.
///
/// The page is rendered with no <c>JobStorage</c>, so the Projected pipeline yields an empty matrix
/// (the toolbar still renders), which is sufficient to assert the selector/source gating.
/// </summary>
public class HeatmapPageJobClassTests
{
    private static HeatmapService CreateHeatmapService(IStorageMetricsProvider provider)
    {
        var services = new ServiceCollection();
        if (provider != null)
        {
            services.AddSingleton(provider);
        }

        services.AddLogging();
        services.AddSingleton<IMemoryCache>(new MemoryCache(new MemoryCacheOptions()));
        services.AddSingleton(new DashboardUIOptions());

        return new HeatmapService(services.BuildServiceProvider());
    }

    private static TestContext NewContext(IStorageMetricsProvider provider, out DashboardUIOptions options)
    {
        var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        options = new DashboardUIOptions();
        options.Heatmap.Enabled = true;

        ctx.Services.AddSingleton(options);
        ctx.Services.AddSingleton(CreateHeatmapService(provider));
        // The Heatmap page @injects HangfireMonitorService (used by the capacity input and the
        // drill-down schedule-save path). Register a storage-mock-backed instance so the page can be
        // instantiated and rendered; the monitor is not exercised by the Job_Class assertions below.
        ctx.Services.AddSingleton(new HangfireMonitorService(new Mock<JobStorage>().Object));
        return ctx;
    }

    private static IEnumerable<IElement> JobClassButtons(IRenderedComponent<HeatmapPage> cut) =>
        cut.FindAll("[aria-label='Job class'] button");

    private static IElement JobClassButton(IRenderedComponent<HeatmapPage> cut, string label) =>
        JobClassButtons(cut).Single(b => b.TextContent.Trim() == label);

    private static bool IsActive(IElement button) => button.ClassList.Contains("btn-primary");

    [Fact]
    public void Selector_OffersExactlyThreeClasses_CronAdHocCombined()
    {
        using var ctx = NewContext(provider: null, out _);

        var cut = ctx.RenderComponent<HeatmapPage>();

        var labels = JobClassButtons(cut).Select(b => b.TextContent.Trim()).ToArray();
        Assert.Equal(new[] { "Cron", "Ad-hoc", "Combined" }, labels);
    }

    [Fact]
    public void WithoutProvider_DefaultsToCron_AndDisablesAdHocAndCombined()
    {
        // Req 16.7 / 16.9: no provider → Ad-hoc and Combined disabled, default class is Cron.
        using var ctx = NewContext(provider: null, out _);

        var cut = ctx.RenderComponent<HeatmapPage>();

        Assert.True(IsActive(JobClassButton(cut, "Cron")));
        Assert.False(IsActive(JobClassButton(cut, "Ad-hoc")));
        Assert.False(IsActive(JobClassButton(cut, "Combined")));

        Assert.True(JobClassButton(cut, "Ad-hoc").HasAttribute("disabled"));
        Assert.True(JobClassButton(cut, "Combined").HasAttribute("disabled"));
        Assert.False(JobClassButton(cut, "Cron").HasAttribute("disabled"));
    }

    [Fact]
    public void WithoutProvider_HidesSourceToggle_DefaultingToProjected()
    {
        // Req 16.7: without a provider the Historical source is unavailable, so the toggle is hidden.
        using var ctx = NewContext(provider: null, out _);

        var cut = ctx.RenderComponent<HeatmapPage>();

        Assert.Empty(cut.FindAll("[aria-label='Data source']"));
    }

    [Fact]
    public void WithProvider_DefaultsToCombined()
    {
        // Req 16.2: Combined is the default Job_Class when a metrics provider is registered.
        var provider = new Mock<IStorageMetricsProvider>().Object;
        using var ctx = NewContext(provider, out _);

        var cut = ctx.RenderComponent<HeatmapPage>();

        Assert.True(IsActive(JobClassButton(cut, "Combined")));
        Assert.False(IsActive(JobClassButton(cut, "Cron")));
        Assert.False(IsActive(JobClassButton(cut, "Ad-hoc")));
    }

    [Fact]
    public void WithProvider_EnablesAdHocAndCombinedAsAPair_AndShowsSourceToggle()
    {
        // Req 16.9: with a provider, Ad-hoc and Combined are enabled together; the source toggle shows.
        var provider = new Mock<IStorageMetricsProvider>().Object;
        using var ctx = NewContext(provider, out _);

        var cut = ctx.RenderComponent<HeatmapPage>();

        Assert.False(JobClassButton(cut, "Ad-hoc").HasAttribute("disabled"));
        Assert.False(JobClassButton(cut, "Combined").HasAttribute("disabled"));
        Assert.Single(cut.FindAll("[aria-label='Data source']"));
    }

    [Fact]
    public void WithProvider_ConfiguredAdHocDefault_ForcesHistoricalSource()
    {
        // Req 16.6: selecting the Ad-hoc class forces the Historical source. Seed it as the default
        // so the forced-source decision is exercised through ApplyDefaults.
        var provider = new Mock<IStorageMetricsProvider>().Object;
        using var ctx = NewContext(provider, out var options);
        options.Heatmap.DefaultJobClass = "Ad-hoc";

        var cut = ctx.RenderComponent<HeatmapPage>();

        Assert.True(IsActive(JobClassButton(cut, "Ad-hoc")));

        // The Historical source button is the active one in the source toggle.
        var sourceButtons = cut.FindAll("[aria-label='Data source'] button");
        var historical = sourceButtons.Single(b => b.TextContent.Trim() == "Historical");
        Assert.True(historical.ClassList.Contains("btn-primary"));
    }
}
