using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Helpers;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests;

public class StackTraceFormatterTests
{
    private const string SampleTrace =
        "at MyApp.Services.WidgetService.Process(Int32 id) in C:\\build\\src\\Services\\WidgetService.cs:line 42";

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, StackTraceFormatter.Format(null));
        Assert.Equal(string.Empty, StackTraceFormatter.Format(""));
    }

    [Fact]
    public void NoSourceLink_RendersPlainFileSpan_NotAnchor()
    {
        var html = StackTraceFormatter.Format(SampleTrace);

        Assert.DoesNotContain("<a ", html);
        Assert.Contains("hf-st-file", html);
        Assert.Contains("hf-st-method", html);
        Assert.Contains("hf-st-line", html);
    }

    [Fact]
    public void HtmlIsEncoded_NoRawAngleBracketsFromInput()
    {
        var malicious = "at Foo.Bar<script>() in /x.cs:line 1";
        var html = StackTraceFormatter.Format(malicious);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void GitHubSourceLink_ProducesAnchorWithLineFragment()
    {
        var options = SourceLinkOptions.GitHub("owner/repo", "main").WithPathStrip("src");

        var html = StackTraceFormatter.Format(SampleTrace, options);

        Assert.Contains("<a ", html);
        Assert.Contains("https://github.com/owner/repo/blob/main/", html);
        Assert.Contains("src/Services/WidgetService.cs", html);
        Assert.Contains("#L42", html);
        Assert.Contains("target=\"_blank\"", html);
        Assert.Contains("rel=\"noopener noreferrer\"", html);
    }

    [Fact]
    public void LocalSourceLink_UsesAbsolutePathAndIdeIcon()
    {
        var options = SourceLinkOptions.Local("vscode");

        var html = StackTraceFormatter.Format(SampleTrace, options);

        Assert.Contains("<a ", html);
        Assert.Contains("vscode://file/", html);
        // forward-slashed absolute path; the drive-letter colon is percent-encoded by EscapeDataString
        Assert.Contains("build/src/Services/WidgetService.cs", html);
        Assert.Contains("bi-window-stack", html);
    }

    [Fact]
    public void UnsafeScheme_IsNotRenderedAsLink()
    {
        var options = new SourceLinkOptions { UrlPattern = "javascript:alert(1)//{path}#{line}" };

        var html = StackTraceFormatter.Format(SampleTrace, options);

        // Must fall back to a plain span, never an anchor with a javascript: href.
        Assert.DoesNotContain("<a ", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.Contains("hf-st-file", html);
    }

    [Fact]
    public void TraceWithoutFileInfo_StillHighlightsMethod()
    {
        var trace = "at MyApp.Services.WidgetService.Process(Int32 id)";
        var html = StackTraceFormatter.Format(trace, SourceLinkOptions.GitHub("o/r"));

        Assert.Contains("hf-st-method", html);
        Assert.DoesNotContain("<a ", html);
    }
}

public class SourceLinkOptionsTests
{
    [Fact]
    public void WithPathStrip_KeepsFromNamedSegment()
    {
        var options = new SourceLinkOptions().WithPathStrip("src");
        var result = options.PathTransform("C:\\jenkins\\workspace\\proj\\src\\Foo.cs");
        Assert.Equal("src/Foo.cs", result);
    }

    [Fact]
    public void WithPathStrip_NoMatch_FallsBackToForwardSlashedPath()
    {
        var options = new SourceLinkOptions().WithPathStrip("nonexistent");
        var result = options.PathTransform("C:\\build\\Foo.cs");
        Assert.Equal("C:/build/Foo.cs", result);
    }

    [Fact]
    public void WithPathStrip_IsCaseInsensitive()
    {
        var options = new SourceLinkOptions().WithPathStrip("SRC");
        var result = options.PathTransform("/home/runner/src/App/Program.cs");
        Assert.Equal("src/App/Program.cs", result);
    }

    [Fact]
    public void WithPathReplace_AppliesRegexAndNormalizesSeparators()
    {
        var options = new SourceLinkOptions()
            .WithPathReplace(@"^.*[\\/]workspace[\\/]", "");
        var result = options.PathTransform("C:\\agent\\workspace\\src\\Foo.cs");
        Assert.Equal("src/Foo.cs", result);
    }

    [Fact]
    public void Presets_ProduceExpectedUrlPatterns()
    {
        Assert.Contains("github.com/o/r/blob/main/", SourceLinkOptions.GitHub("o/r").UrlPattern);
        Assert.Contains("gitlab.com/o/r/-/blob/main/", SourceLinkOptions.GitLab("o/r").UrlPattern);
        Assert.Contains("dev.azure.com/org/proj/_git/repo", SourceLinkOptions.AzureDevOps("org", "proj", "repo").UrlPattern);
        Assert.Contains("bitbucket.org/o/r/src/main/", SourceLinkOptions.Bitbucket("o/r").UrlPattern);
        Assert.Equal(SourceLinkKind.Local, SourceLinkOptions.Local().Kind);
    }
}
