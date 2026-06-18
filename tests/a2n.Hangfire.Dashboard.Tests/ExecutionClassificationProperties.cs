#nullable enable
using System;
using System.Linq;
using FsCheck;
using FsCheck.Xunit;
using Xunit;
using a2n.Hangfire.Dashboard.Heatmap;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Property test for <see cref="ExecutionClassifier.Classify"/>, the pure rule that classifies a
/// historical job execution as <see cref="ExecutionClass.Cron"/> or <see cref="ExecutionClass.AdHoc"/>
/// solely by the presence of a <c>RecurringJobId</c>.
///
/// **Property 24: Execution classification follows RecurringJobId presence**
/// **Validates: Requirements 16.1, 24.1**
///
/// An execution is classified as <see cref="ExecutionClass.Cron"/> if and only if it carries a
/// non-null, non-whitespace <c>RecurringJobId</c>; otherwise (null, empty, or whitespace-only) it is
/// classified as <see cref="ExecutionClass.AdHoc"/> (Req 16.1, 24.1).
/// </summary>
public class ExecutionClassificationProperties
{
    /// <summary>
    /// Whitespace-only candidates (including null) that must classify as <see cref="ExecutionClass.AdHoc"/>.
    /// Mixes the empty string, ASCII spaces/tabs/newlines, and a Unicode non-breaking space.
    /// </summary>
    private static readonly string?[] BlankIds =
    {
        null, "", " ", "   ", "\t", "\n", "\r\n", " \t \r\n ", "\u00A0", "\u2003"
    };

    /// <summary>
    /// Generates a non-empty <c>RecurringJobId</c> guaranteed to contain at least one
    /// non-whitespace character (so it must classify as <see cref="ExecutionClass.Cron"/>), optionally
    /// padded with surrounding whitespace to exercise the trimming semantics of the rule.
    /// </summary>
    private static Gen<string> NonBlankIdGen =>
        from core in Arb.Default.NonWhiteSpaceString().Generator.Select(s => s.Get)
        from leftPad in Gen.Elements("", " ", "\t", "  \n ")
        from rightPad in Gen.Elements("", " ", "\t", " \r\n")
        select leftPad + core + rightPad;

    /// <summary>Generates a blank id (null/empty/whitespace) that must classify as Ad-hoc.</summary>
    private static Gen<string?> BlankIdGen => Gen.Elements(BlankIds);

    /// <summary>
    /// A 50/50 mix of blank and non-blank recurring job ids, so each run exercises both branches of
    /// the classification rule across many inputs.
    /// </summary>
    private static Gen<string?> RecurringJobIdGen =>
        Gen.OneOf(BlankIdGen, NonBlankIdGen.Select(s => (string?)s));

    /// <summary>
    /// **Property 24: Execution classification follows RecurringJobId presence**
    /// **Validates: Requirements 16.1, 24.1**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Classification_FollowsRecurringJobIdPresence()
    {
        return Prop.ForAll(Arb.From(RecurringJobIdGen), recurringJobId =>
        {
            var actual = ExecutionClassifier.Classify(recurringJobId!);

            // The oracle is exactly "has a non-whitespace RecurringJobId" (Req 16.1, 24.1).
            var hasRecurringId = !string.IsNullOrWhiteSpace(recurringJobId);
            var expected = hasRecurringId ? ExecutionClass.Cron : ExecutionClass.AdHoc;

            return (actual == expected).Label(
                $"recurringJobId={Describe(recurringJobId)} -> actual={actual}, expected={expected}");
        });
    }

    private static string Describe(string? value) =>
        value is null ? "<null>" : $"\"{value}\"";
}
