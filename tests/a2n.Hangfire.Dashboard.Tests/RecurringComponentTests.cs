using System;
using Bunit;
using Hangfire.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Services;
using RecurringPage = a2n.Hangfire.Dashboard.Components.Pages.Recurring;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// bunit component tests for the Recurring Jobs list page (<c>Components/Pages/Recurring.razor</c>),
/// covering the client-side filter added for
/// <see href="https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/13">Issue #13</see>.
///
/// <para>
/// The page injects <see cref="HangfireMonitorService"/> and <see cref="DashboardUIOptions"/>; a real
/// service backed by <see cref="InMemoryStorage"/> is registered and seeded with a couple of
/// recurring jobs. JSInterop is set to loose so timestamp/tooltip helpers used by the rows are no-ops
/// in the test renderer. The fixture job type is reused from <c>RecurringEditorComponentTests</c>.
/// </para>
/// </summary>
public class RecurringComponentTests
{
    private static (TestContext ctx, HangfireMonitorService svc) NewContext()
    {
        var storage = new InMemoryStorage();
        var options = new DashboardUIOptions { IsReadOnly = false, EnableJobManagement = true };
        var resolver = new JobMethodResolver();
        var svc = new HangfireMonitorService(storage, null, options, resolver);

        var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(resolver);
        ctx.Services.AddSingleton(svc);
        ctx.Services.AddSingleton(options);
        return (ctx, svc);
    }

    private static void Seed(HangfireMonitorService svc, string jobId)
    {
        var result = svc.CreateOrUpdateRecurringJob(new RecurringJobRequest(
            JobId: jobId,
            TypeName: typeof(RecurringEditorFixtureJob).FullName,
            MethodName: nameof(RecurringEditorFixtureJob.DoNothing),
            ParameterJson: "[]",
            Cron: "0 0 * * *",
            Queue: "default",
            TimeZoneId: null,
            IsCustomMethod: false));
        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public void Lists_All_Recurring_Jobs_When_Filter_Is_Empty()
    {
        var (ctx, svc) = NewContext();
        using var _ = ctx;
        Seed(svc, "alpha-sync");
        Seed(svc, "beta-report");

        var cut = ctx.RenderComponent<RecurringPage>();
        cut.WaitForState(() => cut.FindAll("#recurring-filter").Count > 0, TimeSpan.FromSeconds(5));

        Assert.Contains("alpha-sync", cut.Markup);
        Assert.Contains("beta-report", cut.Markup);
    }

    [Fact]
    public void Filter_Narrows_The_List_By_Job_Id()
    {
        var (ctx, svc) = NewContext();
        using var _ = ctx;
        Seed(svc, "alpha-sync");
        Seed(svc, "beta-report");

        var cut = ctx.RenderComponent<RecurringPage>();
        cut.WaitForState(() => cut.FindAll("#recurring-filter").Count > 0, TimeSpan.FromSeconds(5));

        // Filtering by a substring of one id shows only the matching job (Issue #13).
        cut.Find("#recurring-filter").Input("alpha");

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("alpha-sync", cut.Markup);
            Assert.DoesNotContain("beta-report", cut.Markup);
        }, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Filter_With_No_Matches_Shows_Empty_State()
    {
        var (ctx, svc) = NewContext();
        using var _ = ctx;
        Seed(svc, "alpha-sync");
        Seed(svc, "beta-report");

        var cut = ctx.RenderComponent<RecurringPage>();
        cut.WaitForState(() => cut.FindAll("#recurring-filter").Count > 0, TimeSpan.FromSeconds(5));

        cut.Find("#recurring-filter").Input("no-such-job");

        // A filter that matches nothing surfaces a dedicated empty state (Issue #13).
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("No recurring jobs match", cut.Markup);
            Assert.DoesNotContain("alpha-sync", cut.Markup);
            Assert.DoesNotContain("beta-report", cut.Markup);
        }, TimeSpan.FromSeconds(5));
    }
}
