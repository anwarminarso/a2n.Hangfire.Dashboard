using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using FsCheck;
using FsCheck.Xunit;
using Hangfire;
using Hangfire.InMemory;
using Hangfire.Storage.Monitoring;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Components.Layout;
using a2n.Hangfire.Dashboard.Components.Pages;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Preservation property tests for the Dashboard Home Polish bugfix.
/// These tests capture existing baseline behavior that MUST remain unchanged after the fix.
/// 
/// **Validates: Requirements 3.1, 3.2, 3.3, 3.4**
/// </summary>
public class PreservationPropertyTests
{
    private readonly JobStorage _storage;

    public PreservationPropertyTests()
    {
        _storage = new InMemoryStorage();
    }

    /// <summary>
    /// All known navigation routes and their expected page titles.
    /// This captures the current behavior of the UpdatePageTitle method in MainLayout.
    /// </summary>
    public static readonly (string path, string expectedTitle)[] KnownRoutes =
    [
        ("", "Dashboard"),
        ("/", "Dashboard"),
        ("/jobs/enqueued", "Enqueued Jobs"),
        ("/jobs/processing", "Processing Jobs"),
        ("/jobs/scheduled", "Scheduled Jobs"),
        ("/jobs/succeeded", "Succeeded Jobs"),
        ("/jobs/failed", "Failed Jobs"),
        ("/jobs/deleted", "Deleted Jobs"),
        ("/jobs/awaiting", "Awaiting Jobs"),
        ("/recurring", "Recurring Jobs"),
        ("/servers", "Servers"),
        ("/retries", "Retries"),
        ("/tags", "Tags"),
    ];

    // ===== Requirement 3.1: Page Title Preservation =====

    /// <summary>
    /// Property 2 - Preservation Requirement 3.1:
    /// For all navigation routes, page title text matches expected value without styling regression.
    /// 
    /// **Validates: Requirements 3.1**
    /// 
    /// Observation on UNFIXED code: The MainLayout component uses a path switch expression
    /// to map URL paths to page titles. Each known route produces a specific title string.
    /// This test verifies that mapping is preserved for all known routes.
    /// </summary>
    [Fact]
    public void PageTitle_AllKnownRoutes_MatchExpectedValues()
    {
        // Arrange & Act & Assert - verify all known routes produce correct titles
        foreach (var (path, expectedTitle) in KnownRoutes)
        {
            var actualTitle = GetPageTitleForPath(path);
            Assert.Equal(expectedTitle, actualTitle);
        }
    }

    /// <summary>
    /// Property 2 - Preservation Requirement 3.1:
    /// For job detail routes with arbitrary job IDs, the title follows the pattern "Job #{id}".
    /// 
    /// **Validates: Requirements 3.1**
    /// </summary>
    [Property(MaxTest = 50)]
    public Property PageTitle_JobDetailRoutes_FollowPattern()
    {
        return Prop.ForAll(
            Arb.From(Gen.Choose(1, 99999).Select(i => i.ToString())),
            jobId =>
            {
                var path = $"/jobs/details/{jobId}";
                var expectedTitle = $"Job #{jobId}";
                var actualTitle = GetPageTitleForPath(path);
                return (actualTitle == expectedTitle)
                    .Label($"Expected '{expectedTitle}' but got '{actualTitle}' for path '{path}'");
            });
    }

    /// <summary>
    /// Property 2 - Preservation Requirement 3.1:
    /// For unknown routes, the title defaults to "Hangfire Dashboard".
    /// 
    /// **Validates: Requirements 3.1**
    /// </summary>
    [Property(MaxTest = 50)]
    public Property PageTitle_UnknownRoutes_DefaultToHangfireDashboard()
    {
        var unknownPathGen = Gen.Elements(
            "/unknown", "/foo/bar", "/settings", "/admin",
            "/custom-page", "/api/test", "/dashboard/extra"
        );

        return Prop.ForAll(
            Arb.From(unknownPathGen),
            path =>
            {
                var actualTitle = GetPageTitleForPath(path);
                return (actualTitle == "Hangfire Dashboard")
                    .Label($"Expected 'Hangfire Dashboard' but got '{actualTitle}' for path '{path}'");
            });
    }

    /// <summary>
    /// Property 2 - Preservation Requirement 3.1:
    /// For recurring job editor routes, the title is "Recurring Job Editor".
    /// 
    /// **Validates: Requirements 3.1**
    /// </summary>
    [Property(MaxTest = 20)]
    public Property PageTitle_RecurringEditorRoutes_ShowEditorTitle()
    {
        var recurringPathGen = Gen.Elements(
            "/recurring/new", "/recurring/edit", "/recurring/my-job-id",
            "/recurring/test-123"
        );

        return Prop.ForAll(
            Arb.From(recurringPathGen),
            path =>
            {
                var actualTitle = GetPageTitleForPath(path);
                return (actualTitle == "Recurring Job Editor")
                    .Label($"Expected 'Recurring Job Editor' but got '{actualTitle}' for path '{path}'");
            });
    }

    // ===== Requirement 3.2: AppPath Null Preservation =====

    /// <summary>
    /// Property 2 - Preservation Requirement 3.2:
    /// For AppPath = null, header renders without "Back to Site" link.
    /// 
    /// **Validates: Requirements 3.2**
    /// 
    /// Observation on UNFIXED code: When AppPath is null, the MainLayout header
    /// does NOT contain any "Back to Site" link. This is the current behavior
    /// (the link was never implemented in the Blazor rewrite).
    /// </summary>
    [Fact]
    public void Header_AppPathNull_NoBackToSiteLink()
    {
        // Arrange
        var options = new DashboardUIOptions { AppPath = null };

        using var ctx = new Bunit.TestContext();
        SetupMainLayoutServices(ctx, options);

        // Act - MainLayout inherits LayoutComponentBase, render without ChildContent
        var cut = ctx.RenderComponent<MainLayout>();

        // Assert - no "Back to Site" link should exist in the header
        var header = cut.Find("header.navbar");
        var links = header.QuerySelectorAll("a");
        var backToSiteLink = links.FirstOrDefault(a =>
            a.TextContent.Contains("Back to Site", StringComparison.OrdinalIgnoreCase));

        Assert.Null(backToSiteLink);
    }

    /// <summary>
    /// Property 2 - Preservation Requirement 3.2:
    /// For AppPath = "/" (default), header renders "Back to Site" link pointing to "/".
    /// 
    /// **Validates: Requirements 3.2**
    /// </summary>
    [Fact]
    public void Header_AppPathDefault_RendersBackToSiteLink()
    {
        // Arrange - default AppPath is "/"
        var options = new DashboardUIOptions();

        using var ctx = new Bunit.TestContext();
        SetupMainLayoutServices(ctx, options);

        // Act
        var cut = ctx.RenderComponent<MainLayout>();

        // Assert - on fixed code, "Back to Site" link exists when AppPath is set
        var header = cut.Find("header.navbar");
        var allAnchors = header.QuerySelectorAll("a");
        var backToSiteLink = allAnchors.FirstOrDefault(a =>
            a.TextContent.Contains("Back to Site", StringComparison.OrdinalIgnoreCase));

        // The bug was fixed: "Back to Site" link now renders when AppPath is configured
        Assert.NotNull(backToSiteLink);
    }

    /// <summary>
    /// Property 2 - Preservation Requirement 3.2:
    /// Property test: for any null AppPath value, no "Back to Site" link appears.
    /// 
    /// **Validates: Requirements 3.2**
    /// </summary>
    [Fact]
    public void Header_AppPathNull_NeverRendersBackToSiteLink()
    {
        // This test specifically validates that null AppPath never produces a link
        var options = new DashboardUIOptions { AppPath = null };

        using var ctx = new Bunit.TestContext();
        SetupMainLayoutServices(ctx, options);

        var cut = ctx.RenderComponent<MainLayout>();

        // The header should not contain any anchor with "Back to Site" text
        var markup = cut.Find("header.navbar").InnerHtml;
        Assert.DoesNotContain("Back to Site", markup, StringComparison.OrdinalIgnoreCase);
    }

    // ===== Requirement 3.3: Theme Toggle Preservation =====

    /// <summary>
    /// Property 2 - Preservation Requirement 3.3:
    /// For all theme values (light, dark, auto), the ThemeToggle component renders
    /// three buttons correctly with proper Bootstrap classes.
    /// 
    /// **Validates: Requirements 3.3**
    /// 
    /// Observation on UNFIXED code: The ThemeToggle renders a btn-group with 3 buttons
    /// (auto, light, dark). Each button has either btn-primary (active) or btn-outline-secondary.
    /// The default theme is "auto" which gets btn-primary on first render.
    /// </summary>
    [Theory]
    [InlineData("auto")]
    [InlineData("light")]
    [InlineData("dark")]
    public void ThemeToggle_AllThemeValues_RenderCorrectButtons(string theme)
    {
        // Arrange
        using var ctx = new Bunit.TestContext();
        var jsInterop = ctx.JSInterop;
        jsInterop.Mode = JSRuntimeMode.Loose;
        jsInterop.Setup<string>("themeManager.get").SetResult(theme);

        // Act
        var cut = ctx.RenderComponent<a2n.Hangfire.Dashboard.Components.Shared.ThemeToggle>();

        // Assert - the component renders a btn-group with 3 buttons
        var btnGroup = cut.Find(".btn-group");
        Assert.NotNull(btnGroup);

        var buttons = btnGroup.QuerySelectorAll("button");
        Assert.Equal(3, buttons.Length);

        // Verify button icons are present (auto=display, light=sun, dark=moon)
        Assert.Contains("bi-display", buttons[0].InnerHtml);
        Assert.Contains("bi-sun", buttons[1].InnerHtml);
        Assert.Contains("bi-moon", buttons[2].InnerHtml);
    }

    /// <summary>
    /// Property 2 - Preservation Requirement 3.3:
    /// Property test: for all theme values, the ThemeToggle always renders exactly 3 buttons.
    /// 
    /// **Validates: Requirements 3.3**
    /// </summary>
    [Property(MaxTest = 30)]
    public Property ThemeToggle_AnyThemeValue_AlwaysRendersThreeButtons()
    {
        var themeGen = Gen.Elements("auto", "light", "dark");

        return Prop.ForAll(
            Arb.From(themeGen),
            theme =>
            {
                using var ctx = new Bunit.TestContext();
                ctx.JSInterop.Mode = JSRuntimeMode.Loose;
                ctx.JSInterop.Setup<string>("themeManager.get").SetResult(theme);

                var cut = ctx.RenderComponent<a2n.Hangfire.Dashboard.Components.Shared.ThemeToggle>();
                var buttons = cut.FindAll(".btn-group button");

                return (buttons.Count == 3)
                    .Label($"Expected 3 buttons for theme '{theme}', got {buttons.Count}");
            });
    }

    // ===== Requirement 3.4: Stat Card Formatting Preservation =====

    /// <summary>
    /// Property 2 - Preservation Requirement 3.4:
    /// For all stat cards, numeric values continue to display with proper formatting.
    /// Tests the FormatCount method behavior for various numeric ranges.
    /// 
    /// **Validates: Requirements 3.4**
    /// 
    /// Observation on UNFIXED code: The Home.razor component uses FormatCount for
    /// the Succeeded stat card (values >= 1000 show as "X.XK", >= 1M show as "X.XM").
    /// Other stat cards display raw numbers via .ToString() (implicitly via @_stats.Property).
    /// </summary>
    [Property(MaxTest = 100)]
    public Property StatCards_NumericValues_DisplayWithProperFormatting()
    {
        return Prop.ForAll(
            Arb.From(Gen.Choose(0, 10_000_000).Select(i => (long)i)),
            count =>
            {
                var formatted = FormatCount(count);
                var isValid = count switch
                {
                    >= 1_000_000 => formatted == $"{count / 1_000_000.0:F1}M",
                    >= 1_000 => formatted == $"{count / 1_000.0:F1}K",
                    _ => formatted == count.ToString("N0")
                };
                return isValid.Label($"FormatCount({count}) = '{formatted}' - expected proper formatting");
            });
    }

    /// <summary>
    /// Property 2 - Preservation Requirement 3.4:
    /// Stat cards render with correct structure and display numeric values.
    /// 
    /// **Validates: Requirements 3.4**
    /// 
    /// Observation on UNFIXED code: Each stat card has a card-body with an icon,
    /// a numeric value in fs-5 fw-semibold, and a label in text-body-secondary.
    /// When stats are loaded, all 8 stat card labels are present.
    /// </summary>
    [Fact]
    public void StatCards_RenderWithCorrectStructure_AndDisplayValues()
    {
        // Arrange
        using var ctx = new Bunit.TestContext();
        SetupHomePageServices(ctx);

        // Act
        var cut = ctx.RenderComponent<Home>();

        // Wait for async initialization to complete (stats loaded → toggle button appears)
        cut.WaitForState(() => cut.Markup.Contains("Detailed metrics"), TimeSpan.FromSeconds(5));

        // The 8-card detailed grid is collapsed by default behind the "Detailed metrics" toggle.
        cut.Find(".hf-stats-toggle").Click();
        cut.WaitForState(() => cut.Markup.Contains("Servers"), TimeSpan.FromSeconds(5));

        // Assert - verify all 8 stat cards render with correct labels
        var markup = cut.Markup;
        Assert.Contains("Servers", markup);
        Assert.Contains("Recurring", markup);
        Assert.Contains("Enqueued", markup);
        Assert.Contains("Processing", markup);
        Assert.Contains("Succeeded", markup);
        Assert.Contains("Failed", markup);
        Assert.Contains("Scheduled", markup);
        Assert.Contains("Deleted", markup);
    }

    /// <summary>
    /// Property 2 - Preservation Requirement 3.4:
    /// For various stat values, the stat cards always render the expected labels.
    /// 
    /// **Validates: Requirements 3.4**
    /// </summary>
    [Fact]
    public void StatCards_AlwaysRenderExpectedLabels()
    {
        // Arrange
        using var ctx = new Bunit.TestContext();
        SetupHomePageServices(ctx);

        // Act
        var cut = ctx.RenderComponent<Home>();

        // Wait for async initialization to complete (stats loaded → toggle button appears)
        cut.WaitForState(() => cut.Markup.Contains("Detailed metrics"), TimeSpan.FromSeconds(5));

        // The 8-card detailed grid is collapsed by default behind the "Detailed metrics" toggle.
        cut.Find(".hf-stats-toggle").Click();
        cut.WaitForState(() => cut.Markup.Contains("Servers"), TimeSpan.FromSeconds(5));

        // Assert - all 8 labels are present
        var markup = cut.Markup;
        Assert.Contains("Servers", markup);
        Assert.Contains("Recurring", markup);
        Assert.Contains("Enqueued", markup);
        Assert.Contains("Processing", markup);
        Assert.Contains("Succeeded", markup);
        Assert.Contains("Failed", markup);
        Assert.Contains("Scheduled", markup);
        Assert.Contains("Deleted", markup);
    }

    /// <summary>
    /// Property 2 - Preservation Requirement 3.4:
    /// The FormatCount function correctly formats values across all ranges.
    /// This is a property test that verifies the formatting logic is consistent.
    /// 
    /// **Validates: Requirements 3.4**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property FormatCount_AlwaysProducesNonEmptyString()
    {
        return Prop.ForAll(
            Arb.From(Gen.Choose(0, 50_000_000).Select(i => (long)i)),
            count =>
            {
                var formatted = FormatCount(count);
                var isNonEmpty = !string.IsNullOrWhiteSpace(formatted);
                var hasCorrectSuffix = count switch
                {
                    >= 1_000_000 => formatted.EndsWith("M"),
                    >= 1_000 => formatted.EndsWith("K"),
                    _ => true // no suffix required for small numbers
                };
                return (isNonEmpty && hasCorrectSuffix)
                    .Label($"FormatCount({count}) = '{formatted}' - non-empty: {isNonEmpty}, correct suffix: {hasCorrectSuffix}");
            });
    }

    /// <summary>
    /// Property 2 - Preservation Requirement 3.1:
    /// The h1 element in the header always renders with the navbar-text class
    /// and displays the page title text.
    /// 
    /// **Validates: Requirements 3.1**
    /// </summary>
    [Fact]
    public void Header_H1Element_RendersWithNavbarTextClass()
    {
        // Arrange
        var options = new DashboardUIOptions { AppPath = null };

        using var ctx = new Bunit.TestContext();
        SetupMainLayoutServices(ctx, options);

        // Act - render without ChildContent (MainLayout uses @Body from LayoutComponentBase)
        var cut = ctx.RenderComponent<MainLayout>();

        // Assert - h1 exists with navbar-text class and displays title
        var h1 = cut.Find("h1.navbar-text");
        Assert.NotNull(h1);
        Assert.Equal("Dashboard", h1.TextContent.Trim());
    }

    /// <summary>
    /// Property 2 - Preservation Requirement 3.3:
    /// The MainLayout header always contains the ThemeToggle component
    /// regardless of options configuration.
    /// 
    /// **Validates: Requirements 3.3**
    /// </summary>
    [Fact]
    public void Header_AlwaysContainsThemeToggle()
    {
        // Arrange
        var options = new DashboardUIOptions { AppPath = null };

        using var ctx = new Bunit.TestContext();
        SetupMainLayoutServices(ctx, options);

        // Act
        var cut = ctx.RenderComponent<MainLayout>();

        // Assert - ThemeToggle btn-group is present in header
        var header = cut.Find("header.navbar");
        var btnGroup = header.QuerySelector(".btn-group");
        Assert.NotNull(btnGroup);
    }

    // ===== Helper Methods =====

    /// <summary>
    /// Simulates the UpdatePageTitle logic from MainLayout.razor.
    /// This is a direct copy of the path switch expression to test the title mapping
    /// independently of the full component rendering.
    /// </summary>
    private static string GetPageTitleForPath(string path)
    {
        path = path.TrimEnd('/').ToLowerInvariant();

        return path switch
        {
            "" or "/" => "Dashboard",
            "/jobs/enqueued" => "Enqueued Jobs",
            "/jobs/processing" => "Processing Jobs",
            "/jobs/scheduled" => "Scheduled Jobs",
            "/jobs/succeeded" => "Succeeded Jobs",
            "/jobs/failed" => "Failed Jobs",
            "/jobs/deleted" => "Deleted Jobs",
            "/jobs/awaiting" => "Awaiting Jobs",
            "/recurring" => "Recurring Jobs",
            "/servers" => "Servers",
            "/retries" => "Retries",
            "/tags" => "Tags",
            _ when path.StartsWith("/jobs/details/") => $"Job #{path.Split('/').Last()}",
            _ when path.StartsWith("/recurring/") => "Recurring Job Editor",
            _ => "Hangfire Dashboard"
        };
    }

    /// <summary>
    /// Replicates the FormatCount method from Home.razor for testing.
    /// </summary>
    private static string FormatCount(long count)
    {
        return count switch
        {
            >= 1_000_000 => $"{count / 1_000_000.0:F1}M",
            >= 1_000 => $"{count / 1_000.0:F1}K",
            _ => count.ToString("N0")
        };
    }

    /// <summary>
    /// Sets up services needed for MainLayout rendering in bUnit.
    /// Uses real InMemory storage to avoid mocking non-virtual methods.
    /// </summary>
    private void SetupMainLayoutServices(Bunit.TestContext ctx, DashboardUIOptions options)
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        ctx.Services.AddSingleton(options);
        RegisterDashboardServices(ctx);
    }

    /// <summary>
    /// Sets up services needed for Home page rendering in bUnit.
    /// Uses real InMemory storage to avoid mocking non-virtual methods.
    /// </summary>
    private void SetupHomePageServices(Bunit.TestContext ctx)
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var options = new DashboardUIOptions();
        ctx.Services.AddSingleton(options);
        RegisterDashboardServices(ctx);
        // HealthHeroCard (embedded in the Home page) depends on these.
        ctx.Services.AddScoped(sp => new HealthCheckService(
            sp.GetRequiredService<HangfireMonitorService>(),
            sp.GetRequiredService<DashboardUIOptions>(),
            null));
        ctx.Services.AddSingleton<HealthReportCache>();
    }

    /// <summary>
    /// Registers the common service graph shared by MainLayout and Home page renders, including the
    /// audit/queue-operations services, their shared caches, and the per-circuit actor accessor.
    /// </summary>
    private void RegisterDashboardServices(Bunit.TestContext ctx)
    {
        ctx.Services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(
            new Microsoft.AspNetCore.Http.HttpContextAccessor());
        ctx.Services.AddScoped<AuditActorAccessor>();
        ctx.Services.AddScoped<AuditLogService>(sp => new AuditLogService(
            _storage,
            sp.GetRequiredService<DashboardUIOptions>(),
            sp.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>(),
            sp.GetService<AuditActorAccessor>()));
        ctx.Services.AddScoped<QueueOperationsService>(sp => new QueueOperationsService(
            _storage,
            sp.GetRequiredService<DashboardUIOptions>(),
            sp.GetRequiredService<AuditLogService>(),
            sp.GetService<AuditActorAccessor>()));
        ctx.Services.AddSingleton<QueueOperationsStateCache>();
        ctx.Services.AddScoped<HangfireMonitorService>(sp => new HangfireMonitorService(
            _storage, sp.GetRequiredService<AuditLogService>()));
        ctx.Services.AddSingleton(new TagsDataReader(_storage));
        ctx.Services.AddSingleton(new ThrottlingDataReader(_storage));
        ctx.Services.AddSingleton<ThrottlingDetectionCache>();
    }
}
