#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using FsCheck;
using FsCheck.Xunit;
using a2n.Hangfire.Dashboard.Services.Prometheus;

namespace a2n.Hangfire.Dashboard.Tests.Integrations;

/// <summary>
/// Property tests for <see cref="PrometheusTextFormatter"/>.
///
/// Feature: integrations-v2-6, Property 6: Prometheus exposition validity and HELP/TYPE lines.
///
/// **Property 6** — for any list of metric families, the formatter output parses as valid
/// Prometheus text exposition format 0.0.4, contains exactly one <c># HELP</c> line and exactly
/// one <c># TYPE</c> line per distinct metric family, and the metric names, labels (after
/// unescaping), and sample values are recoverable from the parsed output.
///
/// The test validates the formatter against an <b>independent</b> line-based parser oracle
/// (<see cref="ExpositionParser"/>) implemented from scratch in this file — it does not reuse any
/// of the formatter's own code.
///
/// **Validates: Requirements 5.1, 5.3**
/// </summary>
public class PrometheusTextFormatterProperties
{
    // ──────────────────────────────────────────────────────────────────────
    // Generators
    // ──────────────────────────────────────────────────────────────────────

    private static readonly char[] NameStartChars =
        "abcdefghijklmnopqrstuvwxyz".ToCharArray();

    // Special-character set for label values: quotes, backslashes, newlines, plus other
    // structurally-significant exposition characters and ordinary text.
    private static readonly char[] SpecialValueChars =
        "\"\\\n{},= abcXYZ012".ToCharArray();

    /// <summary>A valid Prometheus metric/label name segment: <c>[a-z][a-z0-9_]*</c>.</summary>
    private static Gen<string> NameSegmentGen =>
        from head in Gen.Elements(NameStartChars)
        from tailLen in Gen.Choose(0, 5)
        from tail in Gen.ArrayOf(tailLen, Gen.Elements("abcdefghijklmnopqrstuvwxyz0123456789_".ToCharArray()))
        select head + new string(tail);

    /// <summary>A finite double that round-trips exactly through the "R" format specifier.</summary>
    private static Gen<double> FiniteValueGen =>
        from whole in Gen.Choose(-1_000_000, 1_000_000)
        from frac in Gen.Choose(0, 999)
        select whole + (frac / 1000.0);

    /// <summary>A label value containing quotes, backslashes, newlines, and other specials.</summary>
    private static Gen<string> LabelValueGen =>
        from len in Gen.Choose(0, 8)
        from chars in Gen.ArrayOf(len, Gen.Elements(SpecialValueChars))
        select new string(chars);

    /// <summary>Help text that may contain backslashes and newlines.</summary>
    private static Gen<string> HelpGen =>
        from len in Gen.Choose(0, 8)
        from chars in Gen.ArrayOf(len, Gen.Elements("\\\n abcHELP.,012".ToCharArray()))
        select new string(chars);

    private static Gen<MetricSample> SampleGen =>
        from labelCount in Gen.Choose(0, 3)
        from keys in Gen.ArrayOf(labelCount, NameSegmentGen)
        from values in Gen.ArrayOf(labelCount, LabelValueGen)
        from value in FiniteValueGen
        select new MetricSample(
            keys.Select((k, i) => new KeyValuePair<string, string>("l_" + i + "_" + k, values[i])).ToList(),
            value);

    private static Gen<MetricType> NonHistogramTypeGen =>
        Gen.Elements(new[] { MetricType.Counter, MetricType.Gauge });

    /// <summary>
    /// Generates a metric family whose name is made unique via the supplied index (families and
    /// histograms draw from disjoint prefixes/indexes so no two declared families share a name).
    /// Sample lists may be empty (Req: empty sample sets).
    /// </summary>
    private static Gen<MetricFamily> FamilyGen(int index) =>
        from seg in NameSegmentGen
        from type in NonHistogramTypeGen
        from help in HelpGen
        from sampleCount in Gen.Choose(0, 4)
        from samples in Gen.ArrayOf(sampleCount, SampleGen)
        select new MetricFamily("f" + index + "_" + seg, type, help, samples.ToList());

    private static Gen<HistogramFamily> HistogramGen(int index) =>
        from seg in NameSegmentGen
        from help in HelpGen
        from bucketCount in Gen.Choose(0, 4)
        from boundsRaw in Gen.ArrayOf(bucketCount, Gen.Choose(1, 5000))
        from countsRaw in Gen.ArrayOf(bucketCount, Gen.Choose(0, 100))
        from sumMillis in Gen.Choose(0, 1_000_000)
        from totalExtra in Gen.Choose(0, 50)
        let bounds = boundsRaw.Select(b => b / 1000.0).Distinct().OrderBy(b => b).ToList()
        // cumulative, monotonically non-decreasing bucket counts
        let cumulative = MakeCumulative(countsRaw.Take(bounds.Count).ToArray())
        let count = (cumulative.Count > 0 ? cumulative[^1] : 0) + totalExtra
        select new HistogramFamily(
            "h" + index + "_" + seg,
            help,
            bounds,
            cumulative,
            sumMillis / 1000.0,
            count);

    private static List<long> MakeCumulative(int[] raw)
    {
        var result = new List<long>(raw.Length);
        long running = 0;
        foreach (var r in raw)
        {
            running += r;
            result.Add(running);
        }

        return result;
    }

    private sealed record ExpositionInput(
        IReadOnlyList<MetricFamily> Families,
        IReadOnlyList<HistogramFamily> Histograms);

    private static Gen<ExpositionInput> InputGen =>
        from familyCount in Gen.Choose(0, 5)
        from histogramCount in Gen.Choose(0, 3)
        from families in Gen.Sequence(Enumerable.Range(0, familyCount).Select(FamilyGen))
        from histograms in Gen.Sequence(Enumerable.Range(0, histogramCount).Select(HistogramGen))
        select new ExpositionInput(families.ToList(), histograms.ToList());

    private static Arbitrary<ExpositionInput> InputArb => Arb.From(InputGen);

    // ──────────────────────────────────────────────────────────────────────
    // Property
    // ──────────────────────────────────────────────────────────────────────

    [Property(MaxTest = 100)]
    public Property Output_Parses_And_Is_Recoverable()
    {
        return Prop.ForAll(InputArb, input =>
        {
            var formatter = new PrometheusTextFormatter();
            var text = formatter.Format(input.Families, input.Histograms);

            var parsed = ExpositionParser.Parse(text);

            // 1. Exactly one HELP and one TYPE line per declared family (counter/gauge).
            foreach (var family in input.Families)
            {
                if (parsed.HelpCount(family.Name) != 1)
                {
                    return Fail($"family '{family.Name}': HELP count = {parsed.HelpCount(family.Name)} (expected 1)", text);
                }

                if (parsed.TypeCount(family.Name) != 1)
                {
                    return Fail($"family '{family.Name}': TYPE count = {parsed.TypeCount(family.Name)} (expected 1)", text);
                }

                var expectedType = family.Type switch
                {
                    MetricType.Counter => "counter",
                    MetricType.Gauge => "gauge",
                    _ => "untyped",
                };
                if (parsed.TypeOf(family.Name) != expectedType)
                {
                    return Fail($"family '{family.Name}': TYPE token = '{parsed.TypeOf(family.Name)}' (expected '{expectedType}')", text);
                }

                // 2. Names, labels (unescaped), and values are recoverable in order.
                var recovered = parsed.SamplesFor(family.Name);
                if (recovered.Count != family.Samples.Count)
                {
                    return Fail($"family '{family.Name}': recovered {recovered.Count} samples (expected {family.Samples.Count})", text);
                }

                for (var i = 0; i < family.Samples.Count; i++)
                {
                    var expected = family.Samples[i];
                    var actual = recovered[i];

                    if (!LabelsEqual(expected.Labels, actual.Labels))
                    {
                        return Fail($"family '{family.Name}' sample {i}: labels mismatch " +
                                    $"expected [{Describe(expected.Labels)}] actual [{Describe(actual.Labels)}]", text);
                    }

                    if (!ValueEqual(expected.Value, actual.Value))
                    {
                        return Fail($"family '{family.Name}' sample {i}: value mismatch expected {expected.Value} actual {actual.Value}", text);
                    }
                }
            }

            // 3. Histograms: one HELP + one TYPE (histogram) per family; _bucket/_sum/_count present.
            foreach (var histogram in input.Histograms)
            {
                if (parsed.HelpCount(histogram.Name) != 1 || parsed.TypeCount(histogram.Name) != 1)
                {
                    return Fail($"histogram '{histogram.Name}': HELP={parsed.HelpCount(histogram.Name)} TYPE={parsed.TypeCount(histogram.Name)} (expected 1/1)", text);
                }

                if (parsed.TypeOf(histogram.Name) != "histogram")
                {
                    return Fail($"histogram '{histogram.Name}': TYPE token = '{parsed.TypeOf(histogram.Name)}' (expected 'histogram')", text);
                }

                var buckets = parsed.SamplesFor(histogram.Name + "_bucket");
                var sums = parsed.SamplesFor(histogram.Name + "_sum");
                var counts = parsed.SamplesFor(histogram.Name + "_count");

                if (buckets.Count == 0)
                {
                    return Fail($"histogram '{histogram.Name}': no _bucket series (expected at least +Inf)", text);
                }

                if (sums.Count != 1 || counts.Count != 1)
                {
                    return Fail($"histogram '{histogram.Name}': _sum={sums.Count} _count={counts.Count} (expected 1/1)", text);
                }

                // The +Inf bucket must equal _count.
                var infBucket = buckets.FirstOrDefault(b =>
                    b.Labels.Any(l => l.Key == "le" && l.Value == "+Inf"));
                if (infBucket is null)
                {
                    return Fail($"histogram '{histogram.Name}': missing +Inf bucket", text);
                }

                if (!ValueEqual(histogram.Count, infBucket.Value) ||
                    !ValueEqual(histogram.Count, counts[0].Value))
                {
                    return Fail($"histogram '{histogram.Name}': +Inf={infBucket.Value} _count={counts[0].Value} (expected {histogram.Count})", text);
                }

                if (!ValueEqual(histogram.Sum, sums[0].Value))
                {
                    return Fail($"histogram '{histogram.Name}': _sum={sums[0].Value} (expected {histogram.Sum})", text);
                }
            }

            return true.ToProperty();
        });
    }

    private static Property Fail(string reason, string text) =>
        false.Label(reason + "\n---- exposition ----\n" + text);

    private static bool LabelsEqual(
        IReadOnlyList<KeyValuePair<string, string>> expected,
        IReadOnlyList<KeyValuePair<string, string>> actual)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }

        for (var i = 0; i < expected.Count; i++)
        {
            if (expected[i].Key != actual[i].Key || expected[i].Value != actual[i].Value)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValueEqual(double expected, double actual) =>
        Math.Abs(expected - actual) <= 1e-9 + 1e-9 * Math.Abs(expected);

    private static string Describe(IReadOnlyList<KeyValuePair<string, string>> labels) =>
        string.Join(",", labels.Select(l => l.Key + "=" + l.Value.Replace("\n", "\\n")));

    // ──────────────────────────────────────────────────────────────────────
    // Independent Prometheus 0.0.4 exposition parser oracle
    // (written from scratch here — does NOT reuse PrometheusTextFormatter code)
    // ──────────────────────────────────────────────────────────────────────

    private sealed record ParsedSample(
        string Name,
        IReadOnlyList<KeyValuePair<string, string>> Labels,
        double Value);

    private sealed class ExpositionParser
    {
        private readonly List<string> _helpNames = new();
        private readonly Dictionary<string, string> _types = new();
        private readonly List<string> _typeNamesRaw = new();
        private readonly List<ParsedSample> _samples = new();

        public int HelpCount(string name) => _helpNames.Count(n => n == name);

        public int TypeCount(string name) => _typeNamesRaw.Count(n => n == name);

        public string? TypeOf(string name) => _types.TryGetValue(name, out var t) ? t : null;

        public IReadOnlyList<ParsedSample> SamplesFor(string name) =>
            _samples.Where(s => s.Name == name).ToList();

        public static ExpositionParser Parse(string text)
        {
            var parser = new ExpositionParser();

            // Lines are terminated by '\n'; real newlines inside label values / help are escaped
            // by the exposition format, so a raw split on '\n' is a valid line tokenizer.
            var lines = text.Split('\n');
            foreach (var line in lines)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith("# HELP ", StringComparison.Ordinal))
                {
                    var rest = line.Substring("# HELP ".Length);
                    var space = rest.IndexOf(' ');
                    var name = space < 0 ? rest : rest.Substring(0, space);
                    parser._helpNames.Add(name);
                }
                else if (line.StartsWith("# TYPE ", StringComparison.Ordinal))
                {
                    var rest = line.Substring("# TYPE ".Length);
                    var space = rest.IndexOf(' ');
                    var name = space < 0 ? rest : rest.Substring(0, space);
                    var type = space < 0 ? string.Empty : rest.Substring(space + 1);
                    parser._typeNamesRaw.Add(name);
                    parser._types[name] = type;
                }
                else if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    // Comment line — ignore.
                }
                else
                {
                    parser._samples.Add(ParseSample(line));
                }
            }

            return parser;
        }

        private static ParsedSample ParseSample(string line)
        {
            var i = 0;

            // Metric name: leading run of [a-zA-Z0-9_:] up to '{' or ' '.
            var nameStart = i;
            while (i < line.Length && line[i] != '{' && line[i] != ' ')
            {
                i++;
            }

            var name = line.Substring(nameStart, i - nameStart);

            var labels = new List<KeyValuePair<string, string>>();

            if (i < line.Length && line[i] == '{')
            {
                i++; // consume '{'
                while (i < line.Length && line[i] != '}')
                {
                    // label key up to '='
                    var keyStart = i;
                    while (i < line.Length && line[i] != '=')
                    {
                        i++;
                    }

                    var key = line.Substring(keyStart, i - keyStart);
                    i++; // consume '='
                    // opening quote
                    // (i now points at '"')
                    i++; // consume '"'

                    var sb = new StringBuilder();
                    while (i < line.Length && line[i] != '"')
                    {
                        if (line[i] == '\\' && i + 1 < line.Length)
                        {
                            var esc = line[i + 1];
                            sb.Append(esc switch
                            {
                                'n' => '\n',
                                '"' => '"',
                                '\\' => '\\',
                                _ => esc,
                            });
                            i += 2;
                        }
                        else
                        {
                            sb.Append(line[i]);
                            i++;
                        }
                    }

                    i++; // consume closing '"'
                    labels.Add(new KeyValuePair<string, string>(key, sb.ToString()));

                    if (i < line.Length && line[i] == ',')
                    {
                        i++; // consume ',' and continue
                    }
                }

                i++; // consume '}'
            }

            // Skip the single separating space, then the value runs to end of line.
            if (i < line.Length && line[i] == ' ')
            {
                i++;
            }

            var valueToken = line.Substring(i);
            var value = valueToken switch
            {
                "+Inf" => double.PositiveInfinity,
                "-Inf" => double.NegativeInfinity,
                "NaN" => double.NaN,
                _ => double.Parse(valueToken, CultureInfo.InvariantCulture),
            };

            return new ParsedSample(name, labels, value);
        }
    }
}
