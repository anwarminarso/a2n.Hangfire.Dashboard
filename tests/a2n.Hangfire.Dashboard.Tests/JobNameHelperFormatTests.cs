using System.Reflection;
using Hangfire;
using Hangfire.Common;
using Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Helpers;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests;

// Regression tests for the JobNameHelper display-name fix.
//
// JobNameHelper now delegates to JobDisplayNameAttribute.Format(context, job) instead of doing a
// manual string.Format over DisplayName. This matches the original Hangfire dashboard
// (HtmlHelper.JobName) and, crucially, routes through the attribute's own (virtual) Format — the
// same entry point that performs ResourceType localization. These tests prove the delegation
// (via a Format override) and that argument placeholder formatting still works.

public class JobNameHelperFormatTests
{
    /// <summary>
    /// A custom <see cref="JobDisplayNameAttribute"/> that overrides <see cref="Format"/>. If the
    /// helper delegates to Format (the fix), this override is observed; the old manual string.Format
    /// path would have ignored it and returned the raw DisplayName.
    /// </summary>
    private sealed class FormatOverrideDisplayNameAttribute : JobDisplayNameAttribute
    {
        public FormatOverrideDisplayNameAttribute(string displayName) : base(displayName) { }

        public override string Format(DashboardContext context, Job job) => "FORMATTED::" + DisplayName;
    }

    private sealed class DisplayNameFixtureJob
    {
        [FormatOverrideDisplayNameAttribute("greeting")]
        public void Overridden() { }

        [JobDisplayName("Hello {0} number {1}")]
        public void Placeholders(string name, int n) { }
    }

    [Fact]
    public void GetDisplayName_DelegatesToAttributeFormat()
    {
        var method = typeof(DisplayNameFixtureJob).GetMethod(nameof(DisplayNameFixtureJob.Overridden))!;
        var job = new Job(typeof(DisplayNameFixtureJob), method, new object[0]);

        var name = JobNameHelper.GetDisplayName(job, null);

        // Proves the helper calls the attribute's (virtual) Format, where ResourceType localization
        // lives — not a manual string.Format over DisplayName.
        Assert.Equal("FORMATTED::greeting", name);
    }

    [Fact]
    public void GetDisplayName_AppliesArgumentPlaceholders()
    {
        var method = typeof(DisplayNameFixtureJob).GetMethod(nameof(DisplayNameFixtureJob.Placeholders))!;
        var job = new Job(typeof(DisplayNameFixtureJob), method, new object[] { "world", 7 });

        var name = JobNameHelper.GetDisplayName(job, null);

        Assert.Equal("Hello world number 7", name);
    }

    // --- Part B: interface fallback ---------------------------------------------------------
    //
    // A job stored against a concrete implementation of an interface job contract carries no
    // method-level [JobDisplayName] (interface-member attributes are not inherited by implementing
    // methods). JobNameHelper now falls back to the interface method so the display name still
    // renders — the visible "test1" bug in the recurring jobs list.

    public interface IDisplayNameContract
    {
        [JobDisplayName("Iface transfer for {0}")]
        void Run(string who);
    }

    public sealed class DisplayNameContractImpl : IDisplayNameContract
    {
        // No [JobDisplayName] here — the attribute lives only on the interface contract.
        public void Run(string who) { }
    }

    [Fact]
    public void GetDisplayName_FallsBackToInterfaceAttribute_WhenConcreteHasNone()
    {
        var method = typeof(DisplayNameContractImpl).GetMethod(nameof(DisplayNameContractImpl.Run))!;
        var job = new Job(typeof(DisplayNameContractImpl), method, new object[] { "server2" });

        var name = JobNameHelper.GetDisplayName(job, null);

        // The interface contract's display name (with placeholder formatting) is used even though
        // the job is stored against the concrete implementation type.
        Assert.Equal("Iface transfer for server2", name);
    }
}
