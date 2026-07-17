#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace a2n.Hangfire.Dashboard.Services.Prometheus;

/// <summary>
/// Serializes Prometheus metric families to the text exposition format, version 0.0.4.
/// <para>
/// This is a pure, dependency-free <see cref="StringBuilder"/> serializer (Req 5.4): it takes
/// no third-party Prometheus client library dependency. For each family it emits exactly one
/// <c># HELP</c> line and one <c># TYPE</c> line (Req 5.3), followed by the family's sample
/// lines. Help text and label values are escaped per the exposition format rules so the output
/// parses as valid 0.0.4 and metric names, labels, and values are recoverable.
/// </para>
/// </summary>
public sealed class PrometheusTextFormatter
{
    /// <summary>
    /// Serializes the supplied counter/gauge families and histogram families to a single
    /// Prometheus text exposition payload (version 0.0.4).
    /// </summary>
    /// <param name="families">The counter and gauge metric families to render.</param>
    /// <param name="histograms">The histogram families to render.</param>
    /// <returns>The exposition payload as a string.</returns>
    public string Format(
        IReadOnlyList<MetricFamily> families,
        IReadOnlyList<HistogramFamily> histograms)
    {
        var sb = new StringBuilder();

        if (families is not null)
        {
            foreach (var family in families)
            {
                if (family is null)
                {
                    continue;
                }

                AppendFamily(sb, family);
            }
        }

        if (histograms is not null)
        {
            foreach (var histogram in histograms)
            {
                if (histogram is null)
                {
                    continue;
                }

                AppendHistogram(sb, histogram);
            }
        }

        return sb.ToString();
    }

    private static void AppendFamily(StringBuilder sb, MetricFamily family)
    {
        AppendHelp(sb, family.Name, family.Help);
        AppendType(sb, family.Name, TypeToken(family.Type));

        if (family.Samples is null)
        {
            return;
        }

        foreach (var sample in family.Samples)
        {
            if (sample is null)
            {
                continue;
            }

            AppendSampleLine(sb, family.Name, sample.Labels, FormatValue(sample.Value));
        }
    }

    private static void AppendHistogram(StringBuilder sb, HistogramFamily histogram)
    {
        AppendHelp(sb, histogram.Name, histogram.Help);
        AppendType(sb, histogram.Name, "histogram");

        var bucketName = histogram.Name + "_bucket";
        var bounds = histogram.BucketBoundsSeconds;
        var counts = histogram.BucketCounts;

        if (bounds is not null && counts is not null)
        {
            var pairCount = Math.Min(bounds.Count, counts.Count);
            for (var i = 0; i < pairCount; i++)
            {
                var labels = new[]
                {
                    new KeyValuePair<string, string>("le", FormatValue(bounds[i])),
                };
                AppendSampleLine(sb, bucketName, labels, FormatValue(counts[i]));
            }
        }

        // The +Inf bucket is cumulative and always equals the total observation count (_count).
        var infLabels = new[]
        {
            new KeyValuePair<string, string>("le", "+Inf"),
        };
        AppendSampleLine(sb, bucketName, infLabels, FormatValue(histogram.Count));

        AppendSampleLine(sb, histogram.Name + "_sum", Array.Empty<KeyValuePair<string, string>>(), FormatValue(histogram.Sum));
        AppendSampleLine(sb, histogram.Name + "_count", Array.Empty<KeyValuePair<string, string>>(), FormatValue(histogram.Count));
    }

    private static void AppendHelp(StringBuilder sb, string name, string? help)
    {
        sb.Append("# HELP ").Append(name).Append(' ').Append(EscapeHelp(help)).Append('\n');
    }

    private static void AppendType(StringBuilder sb, string name, string type)
    {
        sb.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
    }

    private static void AppendSampleLine(
        StringBuilder sb,
        string name,
        IReadOnlyList<KeyValuePair<string, string>>? labels,
        string value)
    {
        sb.Append(name);

        if (labels is not null && labels.Count > 0)
        {
            sb.Append('{');
            for (var i = 0; i < labels.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                var label = labels[i];
                sb.Append(label.Key).Append("=\"").Append(EscapeLabelValue(label.Value)).Append('"');
            }

            sb.Append('}');
        }

        sb.Append(' ').Append(value).Append('\n');
    }

    private static string TypeToken(MetricType type) => type switch
    {
        MetricType.Counter => "counter",
        MetricType.Gauge => "gauge",
        MetricType.Histogram => "histogram",
        _ => "untyped",
    };

    /// <summary>
    /// Escapes HELP text: backslash -&gt; <c>\\</c>, newline -&gt; <c>\n</c>.
    /// </summary>
    private static string EscapeHelp(string? help)
    {
        if (string.IsNullOrEmpty(help))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(help!.Length);
        foreach (var c in help!)
        {
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Escapes label values: backslash -&gt; <c>\\</c>, double-quote -&gt; <c>\"</c>,
    /// newline -&gt; <c>\n</c>.
    /// </summary>
    private static string EscapeLabelValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value!.Length);
        foreach (var c in value!)
        {
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Formats a double using <see cref="CultureInfo.InvariantCulture"/>, mapping the special
    /// IEEE values to their Prometheus representations (<c>+Inf</c>, <c>-Inf</c>, <c>NaN</c>).
    /// </summary>
    private static string FormatValue(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "+Inf";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-Inf";
        }

        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string FormatValue(long value) => value.ToString(CultureInfo.InvariantCulture);
}
