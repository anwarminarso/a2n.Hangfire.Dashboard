using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hangfire.Server;
using a2n.Hangfire.Dashboard.Internal;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests;

// Regression tests for GitHub issue #10 — "Editing a recurring job with parameters throws IntPtr
// serialisation error".
//
// Repro: a job method whose user parameter (ftpName) sits BETWEEN two Hangfire-injected parameters
// (PerformContext, CancellationToken). When the edit form pre-fills the Parameter_JSON from the
// stored Hangfire Args, those Args are positional over ALL declared parameters — so a default
// CancellationToken instance is present. Serializing it directly throws because CancellationToken's
// WaitHandle exposes an IntPtr Handle (System.Text.Json cannot serialize IntPtr).
//
// The fix: RecurringEditor.BuildParameterJson delegates to JobArgumentConverter.ToParameterJsonFromArgs,
// which drops injected slots BEFORE serialization — keeping non-serializable injected values out of
// the serializer and surfacing only the operator-facing parameter(s).
public class Issue10EditPrefillTests
{
    /// <summary>Mirrors the issue's reported signature exactly: ftpName between injected params.</summary>
    private sealed class Issue10FtpJobFixture
    {
        public Task StandardFileTransferServiceAsync(
            PerformContext context, string ftpName, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private static readonly System.Reflection.MethodInfo FtpMethod =
        typeof(Issue10FtpJobFixture).GetMethod(nameof(Issue10FtpJobFixture.StandardFileTransferServiceAsync))!;

    [Fact]
    public void ToParameterJsonFromArgs_ExcludesInjectedParams_AndDoesNotThrow()
    {
        // Stored Args as Hangfire holds them: positional over ALL declared params, so the injected
        // PerformContext (null) and CancellationToken (default) slots are present.
        var storedArgs = new object[] { null, "primary-ftp", CancellationToken.None };

        // The fix must not throw (the IntPtr error in issue #10) and must yield only the user param.
        var json = JobArgumentConverter.ToParameterJsonFromArgs(FtpMethod, storedArgs);

        using var doc = JsonDocument.Parse(json);
        var elements = doc.RootElement.EnumerateArray().ToArray();

        Assert.Single(elements);
        Assert.Equal("primary-ftp", elements[0].GetString());
    }

    [Fact]
    public void NaiveSerialization_OfStoredArgs_Throws_DemonstratingTheBug()
    {
        // Documents WHY the fix is needed: serializing the raw stored Args (which include a
        // CancellationToken) fails — this is the exact failure path the edit form used to hit.
        var storedArgs = new object[] { null, "primary-ftp", CancellationToken.None };

        Assert.ThrowsAny<Exception>(() => JsonSerializer.Serialize(storedArgs));
    }
}
