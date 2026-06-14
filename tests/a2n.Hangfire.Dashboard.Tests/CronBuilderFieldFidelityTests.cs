using System;
using System.Globalization;
using FsCheck;
using FsCheck.Xunit;
using a2n.Hangfire.Dashboard.Internal;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Tests;

// Feature: job-builder, Property 17: For any selection of Cron Builder field values, the produced
// Cron_Expression is Hangfire-compatible and its minute, hour, day-of-month, month, and
// day-of-week fields equal the selected values.

/// <summary>
/// Property test for Cron Builder field fidelity (Property 17).
///
/// Each <see cref="CronFieldSpec"/> records both the field MODE (Every / Specific / Range / Step)
/// and the concrete value(s) that mode requires. <see cref="CronDescriber.Build(CronFields)"/> emits
/// one token per field using that field's legal domain: Every -> <c>*</c>, Specific -> the selected
/// integer, Range -> <c>a-b</c> (collapsing to a single value when a == b), Step -> <c>*/n</c>.
/// This property asserts VALUE fidelity: for an arbitrary <see cref="CronFields"/> whose values are
/// already within their domains, <see cref="CronDescriber.Build(CronFields)"/> renders each output
/// field exactly as selected, AND the resulting expression is a valid, parseable cron.
///
/// **Validates: Requirements 10.2**
/// </summary>
public class CronBuilderFieldFidelityTests
{
    // Per-field legal value domains, positionally: minute, hour, day-of-month, month, day-of-week.
    private static readonly (int Min, int Max)[] Domains =
    {
        (0, 59), (0, 23), (1, 31), (1, 12), (0, 6),
    };

    /// <summary>A single field's selection together with the token it is expected to render as.</summary>
    private readonly record struct FieldCase(CronFieldSpec Spec, string Expected);

    /// <summary>
    /// Generates a field selection within the given domain, paired with the exact token
    /// <see cref="CronDescriber.Build(CronFields)"/> should produce for it.
    /// </summary>
    private static Gen<FieldCase> FieldCaseGen((int Min, int Max) domain) =>
        from mode in Gen.Elements(
            CronFieldMode.Every, CronFieldMode.Specific, CronFieldMode.Range, CronFieldMode.Step)
        from specific in Gen.Choose(domain.Min, domain.Max)
        from a in Gen.Choose(domain.Min, domain.Max)
        from b in Gen.Choose(domain.Min, domain.Max)
        from step in Gen.Choose(1, domain.Max)
        select BuildCase(mode, domain, specific, a, b, step);

    private static FieldCase BuildCase(
        CronFieldMode mode, (int Min, int Max) domain, int specific, int a, int b, int step)
    {
        var lo = Math.Min(a, b);
        var hi = Math.Max(a, b);

        var spec = new CronFieldSpec(mode, specific, lo, hi, step);

        var expected = mode switch
        {
            CronFieldMode.Every => "*",
            CronFieldMode.Specific => specific.ToString(CultureInfo.InvariantCulture),
            CronFieldMode.Range => lo == hi
                ? lo.ToString(CultureInfo.InvariantCulture)
                : $"{lo}-{hi}",
            CronFieldMode.Step => $"*/{step}",
            _ => "*",
        };

        return new FieldCase(spec, expected);
    }

    private static Arbitrary<FieldCase[]> CronFieldCasesArb =>
        Arb.From(
            from minute in FieldCaseGen(Domains[0])
            from hour in FieldCaseGen(Domains[1])
            from dom in FieldCaseGen(Domains[2])
            from month in FieldCaseGen(Domains[3])
            from dow in FieldCaseGen(Domains[4])
            select new[] { minute, hour, dom, month, dow });

    [Property(MaxTest = 200)]
    public Property Build_Fields_EqualSelectedValues_AndExpressionIsParseable()
    {
        return Prop.ForAll(CronFieldCasesArb, cases =>
        {
            var fields = new CronFields(
                cases[0].Spec, cases[1].Spec, cases[2].Spec, cases[3].Spec, cases[4].Spec);

            var cron = CronDescriber.Build(fields);

            var parts = cron.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 5)
            {
                return false.Label($"Expected 5 fields, got {parts.Length} for cron '{cron}'");
            }

            for (var i = 0; i < 5; i++)
            {
                if (parts[i] != cases[i].Expected)
                {
                    return false.Label(
                        $"Field {i} '{parts[i]}' != expected '{cases[i].Expected}' (cron '{cron}')");
                }
            }

            // The produced expression must be a valid, parseable cron.
            var parseable = CronPreview.TryParse(cron, out _);

            return parseable.Label($"Cron '{cron}' was not parseable");
        });
    }
}
