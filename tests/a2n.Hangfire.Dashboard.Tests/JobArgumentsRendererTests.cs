using Hangfire.Common;
using Hangfire.Storage;
using Xunit;
using a2n.Hangfire.Dashboard.Helpers;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Unit tests for <see cref="JobArgumentsRenderer"/> — the argument text used by the job lists and
/// the <c>args:</c> search, and the HTML method-call snippet shown on the job details page.
/// </summary>
public class JobArgumentsRendererTests
{
    // ─── GetArgumentsText ─────────────────────────────────────────────────────

    [Fact]
    public void GetArgumentsText_ReturnsStoredArguments_JoinedWithComma()
    {
        var job = Job.FromExpression(() => SampleArgJobs.SendEmail("user@example.com", 42));
        var invocationData = InvocationData.SerializeJob(job);

        var text = JobArgumentsRenderer.GetArgumentsText(job, invocationData);

        // Stored form: a string argument keeps its JSON quotes, a number does not
        Assert.Equal("\"user@example.com\", 42", text);
    }

    [Fact]
    public void GetArgumentsText_FallsBackToJob_WhenInvocationDataIsNull()
    {
        var job = Job.FromExpression(() => SampleArgJobs.SendEmail("user@example.com", 42));

        var text = JobArgumentsRenderer.GetArgumentsText(job, null);

        Assert.Equal("\"user@example.com\", 42", text);
    }

    [Fact]
    public void GetArgumentsText_MethodWithoutParameters_ReturnsEmptyString()
    {
        var job = Job.FromExpression(() => SampleArgJobs.NoArguments());
        var invocationData = InvocationData.SerializeJob(job);

        Assert.Equal("", JobArgumentsRenderer.GetArgumentsText(job, invocationData));
    }

    [Fact]
    public void GetArgumentsText_NothingAvailable_ReturnsEmptyString()
    {
        Assert.Equal("", JobArgumentsRenderer.GetArgumentsText(null, null));
    }

    [Fact]
    public void GetArgumentsText_UnresolvedJob_StillReturnsStoredArguments()
    {
        // The dashboard host may not reference the job's assembly, leaving Job null
        var invocationData = InvocationData.SerializeJob(
            Job.FromExpression(() => SampleArgJobs.SendEmail("user@example.com", 42)));

        var text = JobArgumentsRenderer.GetArgumentsText(null, invocationData);

        Assert.Equal("\"user@example.com\", 42", text);
    }

    [Fact]
    public void GetArgumentsText_NullArgument_KeepsTheNullKeyword()
    {
        // Hangfire stores a null argument as a JSON null element. The rendered call prints it as the
        // null keyword, so the text form has to as well — otherwise a list row shows an empty slot.
        var job = Job.FromExpression(() => SampleArgJobs.SendEmail(null, 42));
        var invocationData = InvocationData.SerializeJob(job);

        Assert.Equal("null, 42", JobArgumentsRenderer.GetArgumentsText(job, invocationData));
        Assert.Equal("(null, 42)", JobArgumentsRenderer.GetArgumentsSummary(job, invocationData));
    }

    // ─── Summaries ────────────────────────────────────────────────────────────

    [Fact]
    public void GetArgumentsSummary_WrapsArgumentsInParentheses()
    {
        var job = Job.FromExpression(() => SampleArgJobs.SendEmail("user@example.com", 42));
        var invocationData = InvocationData.SerializeJob(job);

        var summary = JobArgumentsRenderer.GetArgumentsSummary(job, invocationData);

        Assert.Equal("(\"user@example.com\", 42)", summary);
    }

    [Fact]
    public void GetArgumentsSummary_MethodWithoutParameters_ReturnsEmptyString()
    {
        var job = Job.FromExpression(() => SampleArgJobs.NoArguments());

        Assert.Equal("", JobArgumentsRenderer.GetArgumentsSummary(job, InvocationData.SerializeJob(job)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SummarizeArgumentsText_EmptyOrWhitespace_ReturnsEmptyString(string text)
    {
        Assert.Equal("", JobArgumentsRenderer.SummarizeArgumentsText(text));
    }

    [Fact]
    public void SummarizeArgumentsText_CollapsesWhitespace()
    {
        var summary = JobArgumentsRenderer.SummarizeArgumentsText("{\n  \"id\": 1\n}");

        Assert.Equal("({ \"id\": 1 })", summary);
    }

    [Fact]
    public void SummarizeArgumentsText_TruncatesWithEllipsis()
    {
        var summary = JobArgumentsRenderer.SummarizeArgumentsText(new string('a', 50), maxLength: 10);

        Assert.Equal("(" + new string('a', 10) + "…)", summary);
    }

    [Fact]
    public void SummarizeArgumentsText_ShorterThanMaxLength_IsNotTruncated()
    {
        var summary = JobArgumentsRenderer.SummarizeArgumentsText("short", maxLength: 10);

        Assert.Equal("(short)", summary);
    }

    // ─── RenderArguments ──────────────────────────────────────────────────────

    [Fact]
    public void RenderArguments_RendersValuesWithoutTheMethodName()
    {
        var job = Job.FromExpression(() => SampleArgJobs.SendEmail("user@example.com", 42));
        var invocationData = InvocationData.SerializeJob(job);

        var html = JobArgumentsRenderer.RenderArguments(job, invocationData);

        Assert.Contains("user@example.com", html);
        Assert.Contains("code-number\">42</span>", html);
        Assert.DoesNotContain("SendEmail", html);
    }

    [Fact]
    public void RenderArguments_UnresolvedJob_RendersRawValues()
    {
        var invocationData = InvocationData.SerializeJob(
            Job.FromExpression(() => SampleArgJobs.SendEmail("user@example.com", 42)));

        var html = JobArgumentsRenderer.RenderArguments(null, invocationData);

        Assert.Contains("user@example.com", html);
        Assert.Contains("42", html);
    }

    // ─── RenderMethodCall ─────────────────────────────────────────────────────

    [Fact]
    public void RenderMethodCall_ResolvedJob_RendersCallWithParameterNameTooltips()
    {
        var job = Job.FromExpression(() => SampleArgJobs.SendEmail("user@example.com", 42));
        var invocationData = InvocationData.SerializeJob(job);

        var html = JobArgumentsRenderer.RenderMethodCall(job, invocationData, "17");

        Assert.Contains("// Id: #17", html);
        Assert.Contains("using", html);
        Assert.Contains("code-method\">SendEmail</span>", html);
        Assert.Contains("user@example.com", html);
        Assert.Contains("code-number\">42</span>", html);
        // Parameter names are rendered as hover tooltips, as in the original dashboard
        Assert.Contains("title=\"recipient\"", html);
        Assert.Contains("title=\"retryCount\"", html);
        Assert.EndsWith(");", html);
    }

    [Fact]
    public void RenderMethodCall_InstanceMethod_RendersActivationLine()
    {
        var job = Job.FromExpression<SampleService>(x => x.Run("payload"));
        var invocationData = InvocationData.SerializeJob(job);

        var html = JobArgumentsRenderer.RenderMethodCall(job, invocationData, "18");

        Assert.Contains("Activate&lt;", html);
        Assert.Contains("sampleService", html);
        Assert.Contains("payload", html);
    }

    [Fact]
    public void RenderMethodCall_UnresolvedJob_RendersCallFromInvocationData()
    {
        var invocationData = InvocationData.SerializeJob(
            Job.FromExpression(() => SampleArgJobs.SendEmail("user@example.com", 42)));

        var html = JobArgumentsRenderer.RenderMethodCall(null, invocationData, "19");

        Assert.Contains("// Id: #19", html);
        Assert.Contains("// Can not find the target method", html);
        Assert.Contains("SampleArgJobs", html);
        Assert.Contains("code-method\">SendEmail</span>", html);
        Assert.Contains("user@example.com", html);
        // Parameter names are unavailable, so declared types are used as tooltips instead
        Assert.Contains("title=\"String\"", html);
        Assert.Contains("title=\"Int32\"", html);
    }

    [Fact]
    public void RenderMethodCall_NoJobAndNoInvocationData_RendersPlaceholder()
    {
        var html = JobArgumentsRenderer.RenderMethodCall(null, null, "20");

        Assert.Contains("// Id: #20", html);
        Assert.Contains("Can not find the target method.", html);
    }

    [Fact]
    public void RenderMethodCall_EncodesHtmlInArgumentValues()
    {
        var job = Job.FromExpression(() => SampleArgJobs.ProcessOrder("<script>alert(1)</script>"));
        var invocationData = InvocationData.SerializeJob(job);

        var html = JobArgumentsRenderer.RenderMethodCall(job, invocationData, "21");

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void RenderMethodCall_ArgumentLargerThanCap_RendersPlaceholder()
    {
        var job = Job.FromExpression(() => SampleArgJobs.ProcessOrder(
            new string('x', JobArgumentsRenderer.MaxArgumentToRenderSize + 1)));
        var invocationData = InvocationData.SerializeJob(job);

        var html = JobArgumentsRenderer.RenderMethodCall(job, invocationData, "22");

        Assert.Contains("VALUE IS TOO BIG", html);
    }

    [Fact]
    public void RenderMethodCall_FewerStoredArgumentsThanParameters_RendersNoValue()
    {
        var job = Job.FromExpression(() => SampleArgJobs.SendEmail("user@example.com", 42));
        var serialized = InvocationData.SerializeJob(job);

        // Simulate a payload written before a parameter was added to the method
        var invocationData = new InvocationData(
            serialized.Type,
            serialized.Method,
            serialized.ParameterTypes,
            SerializationHelper.Serialize(new[] { "\"user@example.com\"" }));

        var html = JobArgumentsRenderer.RenderMethodCall(job, invocationData, "23");

        Assert.Contains("NO VALUE", html);
    }

    // Sample job classes for creating Job instances in tests
    public static class SampleArgJobs
    {
        public static void SendEmail(string recipient, int retryCount) { }
        public static void ProcessOrder(string orderId) { }
        public static void NoArguments() { }
    }

    public class SampleService
    {
        public void Run(string payload) { }
    }
}
