using System.IO;
using System.Text.RegularExpressions;
using Bunit;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Components.Layout;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Bug Condition Exploration Tests for Dashboard Home Visual Defects.
/// 
/// These tests encode the EXPECTED (fixed) behavior and are designed to FAIL
/// on unfixed code, confirming the bugs exist.
/// 
/// Property 1: Bug Condition - Dashboard Home Visual Defects
/// 
/// Bug conditions tested:
/// 1. h1 in navbar header should have border: none; outline: none (CSS rule missing)
/// 2. Header should render "Back to Site" link when AppPath is configured (link missing)
/// 3. Stat cards on home page should have hf-stat-card-* gradient classes (classes missing)
/// 
/// **Validates: Requirements 1.1, 1.2, 1.3**
/// </summary>
public class BugConditionExplorationTests : TestContext
{
    /// <summary>
    /// Resolves the path to the source project root relative to the test assembly location.
    /// </summary>
    private static string GetSourceProjectRoot()
    {
        // Navigate from test bin output to source project
        var testDir = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        return Path.Combine(repoRoot, "src", "a2n.Hangfire.Dashboard");
    }

    /// <summary>
    /// Bug Condition: input.element == "h1" AND input.parentContext == "navbar-header"
    /// 
    /// The h1 element in the header navbar should have CSS rule explicitly setting
    /// border: none; outline: none; to prevent Bootstrap navbar-text interaction
    /// from rendering unwanted borders.
    /// 
    /// EXPECTED TO FAIL on unfixed code: The CSS file does not contain this rule.
    /// Counterexample: Title h1 renders with visible border/outline from Bootstrap navbar-text interaction.
    /// </summary>
    [Fact]
    public void TitleH1_InNavbarHeader_ShouldHaveNoBorderOutlineCssRule()
    {
        // Arrange
        var cssPath = Path.Combine(GetSourceProjectRoot(), "Content", "css", "app.css");
        var cssContent = File.ReadAllText(cssPath);

        // Act & Assert
        // The CSS should contain a rule that targets h1 inside header.navbar
        // and sets border: none and outline: none
        var hasBorderNoneRule = Regex.IsMatch(cssContent,
            @"header\.navbar\s+h1\s*\{[^}]*border\s*:\s*none",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var hasOutlineNoneRule = Regex.IsMatch(cssContent,
            @"header\.navbar\s+h1\s*\{[^}]*outline\s*:\s*none",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        Assert.True(hasBorderNoneRule,
            "Bug 1.1 confirmed: CSS file does not contain 'header.navbar h1 { border: none; ... }' rule. " +
            "The h1 title element inherits unwanted border styling from Bootstrap's navbar-text class.");

        Assert.True(hasOutlineNoneRule,
            "Bug 1.1 confirmed: CSS file does not contain 'header.navbar h1 { ... outline: none; }' rule. " +
            "The h1 title element may show unwanted outline from browser defaults in navbar context.");
    }

    /// <summary>
    /// Bug Condition: input.element == "header" AND input.options.AppPath != null AND NOT backToSiteLinkRendered(input)
    /// 
    /// When AppPath is configured (non-null), the header should render a "Back to Site"
    /// anchor element that navigates to the configured AppPath.
    /// 
    /// EXPECTED TO FAIL on unfixed code: MainLayout does not inject DashboardUIOptions
    /// and does not render any "Back to Site" link.
    /// Counterexample: Header contains no anchor element for "Back to Site" navigation despite AppPath being set.
    /// </summary>
    [Fact]
    public void Header_WithAppPathConfigured_ShouldRenderBackToSiteLink()
    {
        // Arrange
        var razorPath = Path.Combine(GetSourceProjectRoot(), "Components", "Layout", "MainLayout.razor");
        var razorContent = File.ReadAllText(razorPath);

        // Act & Assert
        // The MainLayout should inject DashboardUIOptions
        var injectsOptions = razorContent.Contains("@inject DashboardUIOptions") ||
                             razorContent.Contains("@inject a2n.Hangfire.Dashboard.DashboardUIOptions");

        Assert.True(injectsOptions,
            "Bug 1.2 confirmed: MainLayout.razor does not inject DashboardUIOptions. " +
            "Without injecting the options, the component cannot access AppPath to render the 'Back to Site' link.");

        // The MainLayout should contain a "Back to Site" link/anchor
        var hasBackToSiteLink = razorContent.Contains("Back to Site", StringComparison.OrdinalIgnoreCase);

        Assert.True(hasBackToSiteLink,
            "Bug 1.2 confirmed: MainLayout.razor does not contain 'Back to Site' link text. " +
            "Header has no anchor element for 'Back to Site' navigation despite AppPath being available.");
    }

    /// <summary>
    /// Bug Condition: input.element == "stat-card" AND input.page == "home" AND NOT hasGradientBackground(input)
    /// 
    /// The stat cards on the home page should include hf-stat-card-* gradient background
    /// classes for visual differentiation.
    /// 
    /// EXPECTED TO FAIL on unfixed code: Home.razor only uses border-* classes without
    /// any gradient background styling.
    /// Counterexample: Stat cards have only border-* classes with no gradient background styling.
    /// </summary>
    [Fact]
    public void StatCards_OnHomePage_ShouldHaveGradientBackgroundClasses()
    {
        // Arrange
        var razorPath = Path.Combine(GetSourceProjectRoot(), "Components", "Pages", "Home.razor");
        var razorContent = File.ReadAllText(razorPath);

        // Act & Assert
        // The Home.razor should contain hf-stat-card-* gradient classes
        var hasInfoGradient = razorContent.Contains("hf-stat-card-info");
        var hasWarningGradient = razorContent.Contains("hf-stat-card-warning");
        var hasSuccessGradient = razorContent.Contains("hf-stat-card-success");
        var hasDangerGradient = razorContent.Contains("hf-stat-card-danger");
        var hasNeutralGradient = razorContent.Contains("hf-stat-card-neutral");

        Assert.True(hasInfoGradient,
            "Bug 1.3 confirmed: Home.razor does not use 'hf-stat-card-info' class. " +
            "Enqueued stat card has only 'border-info' with no gradient background.");

        Assert.True(hasWarningGradient,
            "Bug 1.3 confirmed: Home.razor does not use 'hf-stat-card-warning' class. " +
            "Processing stat card has only 'border-warning' with no gradient background.");

        Assert.True(hasSuccessGradient,
            "Bug 1.3 confirmed: Home.razor does not use 'hf-stat-card-success' class. " +
            "Succeeded stat card has only 'border-success' with no gradient background.");

        Assert.True(hasDangerGradient,
            "Bug 1.3 confirmed: Home.razor does not use 'hf-stat-card-danger' class. " +
            "Failed stat card has only 'border-danger' with no gradient background.");

        Assert.True(hasNeutralGradient,
            "Bug 1.3 confirmed: Home.razor does not use 'hf-stat-card-neutral' class. " +
            "Servers/Recurring/Scheduled/Deleted stat cards have no gradient background.");
    }

    /// <summary>
    /// Property-based test: For any non-null AppPath string, the MainLayout source
    /// should contain conditional rendering logic that produces a "Back to Site" link.
    /// 
    /// This property test generates random AppPath values and verifies the component
    /// template contains the necessary conditional rendering pattern.
    /// 
    /// EXPECTED TO FAIL on unfixed code: No conditional AppPath rendering exists.
    /// 
    /// **Validates: Requirements 1.2**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property Header_WithAnyNonNullAppPath_ShouldHaveConditionalBackToSiteRendering()
    {
        return Prop.ForAll(
            Arb.From(Gen.Elements("/app", "/", "/my-app", "/dashboard/../home", "/custom-path")),
            (string appPath) =>
            {
                var razorPath = Path.Combine(GetSourceProjectRoot(), "Components", "Layout", "MainLayout.razor");
                var razorContent = File.ReadAllText(razorPath);

                // The template should have conditional logic checking AppPath
                var hasAppPathConditional = razorContent.Contains("Options.AppPath") ||
                                            razorContent.Contains("AppPath is not null") ||
                                            razorContent.Contains("AppPath != null");

                return hasAppPathConditional.Label(
                    $"Bug 1.2: MainLayout has no conditional AppPath check for rendering 'Back to Site' link. " +
                    $"Tested with AppPath='{appPath}' — no link would be rendered.");
            });
    }

    /// <summary>
    /// Property-based test: For each stat card semantic category (info, warning, success,
    /// danger, neutral), the CSS file should define a corresponding hf-stat-card-* class
    /// with gradient background styling.
    /// 
    /// EXPECTED TO FAIL on unfixed code: No hf-stat-card-* CSS classes exist.
    /// 
    /// **Validates: Requirements 1.3**
    /// </summary>
    [Property(MaxTest = 5)]
    public Property StatCards_ForEachCategory_ShouldHaveGradientCssClassDefined()
    {
        return Prop.ForAll(
            Arb.From(Gen.Elements("info", "warning", "success", "danger", "neutral")),
            (string category) =>
            {
                var cssPath = Path.Combine(GetSourceProjectRoot(), "Content", "css", "app.css");
                var cssContent = File.ReadAllText(cssPath);

                var className = $"hf-stat-card-{category}";
                var hasClass = cssContent.Contains($".{className}");

                return hasClass.Label(
                    $"Bug 1.3: CSS file does not define '.{className}' class. " +
                    $"Stat cards for '{category}' category have no gradient background styling defined.");
            });
    }

    /// <summary>
    /// Property-based test: The h1 element in the header should be styled without
    /// border/outline regardless of the page title content.
    /// 
    /// EXPECTED TO FAIL on unfixed code: No CSS rule exists for h1 in header.
    /// 
    /// **Validates: Requirements 1.1**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property TitleH1_ForAnyPageTitle_ShouldHaveNoBorderCssRule()
    {
        return Prop.ForAll(
            Arb.From(Gen.Elements("Dashboard", "Enqueued Jobs", "Processing Jobs", "Servers", "Failed Jobs",
                "Job #123", "Recurring Jobs", "Tags", "Retries", "Hangfire Dashboard")),
            (string pageTitle) =>
            {
                var cssPath = Path.Combine(GetSourceProjectRoot(), "Content", "css", "app.css");
                var cssContent = File.ReadAllText(cssPath);

                // Regardless of what page title is displayed, the CSS should have
                // a rule preventing border/outline on h1 in the header navbar
                var hasHeaderH1Rule = Regex.IsMatch(cssContent,
                    @"header[^{]*h1\s*\{[^}]*(border\s*:\s*none|outline\s*:\s*none)",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);

                return hasHeaderH1Rule.Label(
                    $"Bug 1.1: No CSS rule found for h1 in header to remove border/outline. " +
                    $"Page title '{pageTitle}' would render with unwanted border from Bootstrap navbar-text interaction.");
            });
    }
}
