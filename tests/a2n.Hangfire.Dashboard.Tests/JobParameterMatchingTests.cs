using a2n.Hangfire.Dashboard.Storage;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests;

public class JobParameterMatchingTests
{
    [Fact]
    public void SerializeUserString_WrapsPlainTextInJsonQuotes()
    {
        var serialized = JobParameterMatching.SerializeUserString("House Keeping");
        Assert.Equal("\"House Keeping\"", serialized);
    }

    [Fact]
    public void AllValueForms_IncludesPlainAndSerialized()
    {
        var forms = JobParameterMatching.AllValueForms(new[] { "simple-job" });
        Assert.Contains("simple-job", forms);
        Assert.Contains("\"simple-job\"", forms);
    }

    [Fact]
    public void BuildStoredValueToPlainIdLookup_ResolvesSerializedFormInO1()
    {
        var lookup = JobParameterMatching.BuildStoredValueToPlainIdLookup(new[] { "automation-sch-app-33-11" });
        var stored = JobParameterMatching.SerializeUserString("automation-sch-app-33-11");

        Assert.True(lookup.TryGetValue(stored, out var plain));
        Assert.Equal("automation-sch-app-33-11", plain);
    }

    [Fact]
    public void ResolvePlainRecurringJobId_UsesLookupBeforeDeserializing()
    {
        var plainIds = new[] { "House Keeping" };
        var lookup = JobParameterMatching.BuildStoredValueToPlainIdLookup(plainIds);
        var stored = JobParameterMatching.SerializeUserString("House Keeping");

        var resolved = JobParameterMatching.ResolvePlainRecurringJobId(stored, lookup);
        Assert.Equal("House Keeping", resolved);
    }
}
