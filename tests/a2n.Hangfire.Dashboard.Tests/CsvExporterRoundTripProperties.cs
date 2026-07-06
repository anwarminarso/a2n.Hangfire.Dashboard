using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using FsCheck;
using FsCheck.Xunit;
using Xunit;
using a2n.Hangfire.Dashboard.Heatmap;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Property tests for <see cref="CsvExporter.Export"/>, the pure serializer that renders a
/// <see cref="HeatmapMatrix"/> to a self-describing RFC 4180 CSV document.
///
/// **Property 22: CSV output round-trips through an RFC 4180 parser**
/// **Validates: Requirements 12.5**
///
/// For any matrix whose queue names (and the export context's viewer time-zone and queue-selection
/// fields) may contain commas, double quotes, carriage returns, or line feeds, parsing the exported
/// document with an independent RFC 4180-compliant parser recovers <em>exactly</em> the original
/// field values — confirming that fields needing escaping are wrapped in double quotes with embedded
/// quotes doubled, and that nothing the exporter writes is mis-parsed (Req 12.5).
///
/// The parser below is a compact, self-contained RFC 4180 reader written for the test (no new
/// dependency is taken). The expected field values are reconstructed independently of the exporter's
/// string-building so the assertion checks a genuine round-trip rather than re-deriving the
/// exporter's own escaped output.
/// </summary>
public class CsvExporterRoundTripProperties
{
    /// <summary>
    /// Building blocks for adversarial strings: ordinary text interleaved with every character class
    /// that forces RFC 4180 quoting (comma, double quote, CR, LF) plus combinations of them.
    /// </summary>
    private static readonly string[] AdversarialAtoms =
    {
        "", "q", "alpha", "default",
        ",", "\"", "\r", "\n", "\r\n",
        "a,b", "say \"hi\"", "line1\nline2", "x\r\ny",
        "trailing,", "\"wrapped\"", "comma, and \"quote\"", "café",
    };

    /// <summary>A string assembled from 0..3 adversarial atoms, so embedded specials appear anywhere.</summary>
    private static Gen<string> AdversarialStringGen =>
        from n in Gen.Choose(0, 3)
        from atoms in Gen.ArrayOf(n, Gen.Elements(AdversarialAtoms))
        select string.Concat(atoms);

    /// <summary>A nullable adversarial string (the viewer time-zone and queue-list entries may be null).</summary>
    private static Gen<string> NullableAdversarialStringGen =>
        Gen.Frequency(
            Tuple.Create(1, Gen.Constant<string>(null)),
            Tuple.Create(6, AdversarialStringGen));

    /// <summary>
    /// A small pool of adversarial queue names so generated cells collide on the same queue and the
    /// queue field (the chief carrier of special characters in data rows) is exercised heavily.
    /// </summary>
    private static Gen<string[]> QueuePoolGen =>
        from n in Gen.Choose(1, 4)
        from queues in Gen.ArrayOf(n, AdversarialStringGen)
        select queues;

    private static Gen<HeatmapSource> SourceGen =>
        Gen.Elements(HeatmapSource.Projected, HeatmapSource.Historical);

    private static Gen<LoadMetric> MetricGen =>
        Gen.Elements(LoadMetric.FireCount, LoadMetric.WorkerMinutes);

    private static Gen<ProjectionWindowKind> KindGen =>
        Gen.Elements(ProjectionWindowKind.IdealizedWeek, ProjectionWindowKind.Next7Days);

    /// <summary>
    /// A projection window anchored on a real calendar date (so day-label resolution succeeds), or
    /// <c>null</c> with low frequency to exercise the <c>Day {index}</c> fallback path (Req 12.7).
    /// </summary>
    private static Gen<ProjectionWindow> WindowGen =>
        Gen.Frequency(
            Tuple.Create(1, Gen.Constant<ProjectionWindow>(null)),
            Tuple.Create(8,
                from kind in KindGen
                from dayOffset in Gen.Choose(0, 4000)
                let start = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(dayOffset)
                select new ProjectionWindow(start, start.AddDays(7), kind)));

    /// <summary>The context's queue selection: a possibly-null list whose entries may be null/adversarial.</summary>
    private static Gen<IReadOnlyList<string>> ContextQueuesGen =>
        Gen.Frequency(
            Tuple.Create(1, Gen.Constant<IReadOnlyList<string>>(null)),
            Tuple.Create(6,
                from n in Gen.Choose(0, 4)
                from items in Gen.ArrayOf(n, NullableAdversarialStringGen)
                select (IReadOnlyList<string>)items));

    /// <summary>
    /// A single cell descriptor: an index into the queue pool, a day index in the realistic 0..6
    /// window domain, an hour 0..23, and a value scaled to include both integral and fractional
    /// magnitudes (so the <c>"R"</c> round-trippable value formatting is exercised).
    /// </summary>
    private static Gen<(int QueueIndex, int DayIndex, int Hour, double Value)> CellDescGen =>
        from queueIndex in Gen.Choose(0, 3)
        from dayIndex in Gen.Choose(0, 6)
        from hour in Gen.Choose(0, 23)
        from valueHundredths in Gen.Choose(0, 5_000_00)
        select (queueIndex, dayIndex, hour, valueHundredths / 100d);

    /// <summary>
    /// **Property 22: CSV output round-trips through an RFC 4180 parser**
    /// **Validates: Requirements 12.5**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property CsvExport_RoundTripsThrough_Rfc4180Parser()
    {
        var arb = Arb.From(
            from source in SourceGen
            from window in WindowGen
            from tz in NullableAdversarialStringGen
            from contextQueues in ContextQueuesGen
            from metric in MetricGen
            from queuePool in QueuePoolGen
            from cellCount in Gen.Choose(0, 40)
            from cellDescs in Gen.ArrayOf(cellCount, CellDescGen)
            select (source, window, tz, contextQueues, metric, queuePool, cellDescs));

        return Prop.ForAll(arb, input =>
        {
            var (source, window, tz, contextQueues, metric, queuePool, cellDescs) = input;

            // Build the matrix: distinct cells keyed by (queue, dayIndex, hour). The exporter only
            // reads each cell's Key/Value and the matrix's Window, so the remaining cell fields are
            // filled with arbitrary-but-valid values.
            var cells = new Dictionary<CellKey, HeatmapCell>();
            foreach (var d in cellDescs)
            {
                var queue = queuePool[d.QueueIndex % queuePool.Length];
                var key = new CellKey(queue, d.DayIndex, d.Hour);
                cells[key] = new HeatmapCell(key, d.Value, 1, queue, Array.Empty<string>());
            }

            var matrix = new HeatmapMatrix(
                cells,
                Queues: cells.Keys.Select(k => k.Queue).Distinct().ToList(),
                Window: window,
                Metric: metric,
                Min: 0d,
                Max: 0d);

            var context = new CsvExportContext(source, window, tz, contextQueues, metric);

            var csv = CsvExporter.Export(matrix, context);

            // Decode the produced document with an independent RFC 4180 parser.
            var parsed = ParseRfc4180(csv);

            // Reconstruct, independently of the exporter's string-building, the field values the
            // document is contractually required to carry, then assert they survive the round-trip.
            var expected = ExpectedRecords(matrix, context);

            if (parsed.Count != expected.Count)
            {
                return false.Label(
                    $"record count mismatch: parsed={parsed.Count} expected={expected.Count}");
            }

            for (var r = 0; r < expected.Count; r++)
            {
                var expRow = expected[r];
                var gotRow = parsed[r];

                if (gotRow.Count != expRow.Count)
                {
                    return false.Label(
                        $"row {r} field count mismatch: parsed={gotRow.Count} expected={expRow.Count} " +
                        $"(parsed=[{string.Join("|", gotRow.Select(Display))}])");
                }

                for (var f = 0; f < expRow.Count; f++)
                {
                    if (!string.Equals(gotRow[f], expRow[f], StringComparison.Ordinal))
                    {
                        return false.Label(
                            $"row {r} field {f} did not round-trip: " +
                            $"parsed={Display(gotRow[f])} expected={Display(expRow[f])}");
                    }
                }
            }

            return true.ToProperty();
        });
    }

    /// <summary>
    /// Anchor example: a queue name containing a comma, a double quote, and a CRLF round-trips
    /// exactly through the parser, and lands as a single field rather than being split on its
    /// embedded comma or newline (Req 12.5).
    /// </summary>
    [Fact]
    public void AdversarialQueueName_RoundTrips_AsASingleField()
    {
        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var window = new ProjectionWindow(start, start.AddDays(7), ProjectionWindowKind.IdealizedWeek);

        const string nastyQueue = "a,b \"c\"\r\nd";
        var key = new CellKey(nastyQueue, 0, 9);
        var cells = new Dictionary<CellKey, HeatmapCell>
        {
            [key] = new HeatmapCell(key, 12.5d, 1, nastyQueue, Array.Empty<string>()),
        };

        var matrix = new HeatmapMatrix(cells, new[] { nastyQueue }, window, LoadMetric.FireCount, 0d, 0d);
        var context = new CsvExportContext(
            HeatmapSource.Projected, window, "Region/\"City\",X", new[] { nastyQueue }, LoadMetric.FireCount);

        var parsed = ParseRfc4180(CsvExporter.Export(matrix, context));

        // The single data row is the final record; its first field is the verbatim queue name.
        var dataRow = parsed[parsed.Count - 1];
        Assert.Equal(4, dataRow.Count);
        Assert.Equal(nastyQueue, dataRow[0]);
        Assert.Equal("9", dataRow[2]);
        Assert.Equal("12.5", dataRow[3]);

        // The adversarial viewer time zone lands intact in its metadata row.
        var tzRow = parsed[2];
        Assert.Equal("# Viewer time zone", tzRow[0]);
        Assert.Equal("Region/\"City\",X", tzRow[1]);
    }

    // ----------------------------------------------------------------------------------------------
    // Independent oracle: the exact field values the exporter is contractually required to write.
    // ----------------------------------------------------------------------------------------------

    private static List<List<string>> ExpectedRecords(HeatmapMatrix matrix, CsvExportContext context)
    {
        var records = new List<List<string>>
        {
            new() { "# Source", DescribeSource(context.Source) },
            new() { "# Window", DescribeWindow(context.Window) },
            new() { "# Viewer time zone", DescribeTimeZone(context.ViewerTimeZoneId) },
            new() { "# Queues", DescribeQueues(context.Queues) },
            new() { "# Load metric", DescribeMetric(context.Metric) },
            new() { "queue", "day", "hour", "value" },
        };

        var ordered = matrix.Cells.Values
            .OrderBy(c => c.Key.Queue, StringComparer.Ordinal)
            .ThenBy(c => c.Key.DayIndex)
            .ThenBy(c => c.Key.Hour);

        foreach (var cell in ordered)
        {
            records.Add(new List<string>
            {
                cell.Key.Queue ?? string.Empty,
                DayLabel(matrix.Window, cell.Key.DayIndex),
                cell.Key.Hour.ToString(CultureInfo.InvariantCulture),
                cell.Value.ToString("R", CultureInfo.InvariantCulture),
            });
        }

        return records;
    }

    private static string DescribeSource(HeatmapSource source)
        => source == HeatmapSource.Historical ? "Historical" : "Projected";

    private static string DescribeWindow(ProjectionWindow window)
    {
        if (window is null)
        {
            return string.Empty;
        }

        return window.Kind == ProjectionWindowKind.Next7Days ? "Next 7 days" : "Idealized week";
    }

    private static string DescribeTimeZone(string viewerTimeZoneId)
        => string.IsNullOrWhiteSpace(viewerTimeZoneId) ? "UTC" : viewerTimeZoneId;

    private static string DescribeQueues(IReadOnlyList<string> queues)
    {
        if (queues is null || queues.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(", ", queues.Select(q => q ?? string.Empty));
    }

    private static string DescribeMetric(LoadMetric metric)
        => metric == LoadMetric.WorkerMinutes ? "Worker-minutes" : "Fire count";

    private static string DayLabel(ProjectionWindow window, int dayIndex)
    {
        try
        {
            if (window is null)
            {
                return "Day " + dayIndex.ToString(CultureInfo.InvariantCulture);
            }

            if (window.Kind == ProjectionWindowKind.Next7Days)
            {
                var date = window.StartInclusive.Date.AddDays(dayIndex);
                return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            var startDow = (int)window.StartInclusive.DayOfWeek;
            var dow = (DayOfWeek)(((startDow + dayIndex) % 7 + 7) % 7);
            return CultureInfo.InvariantCulture.DateTimeFormat.GetDayName(dow);
        }
        catch
        {
            return "Day " + dayIndex.ToString(CultureInfo.InvariantCulture);
        }
    }

    // ----------------------------------------------------------------------------------------------
    // A compact, self-contained RFC 4180 parser. Records are separated by CRLF outside of quotes;
    // a field may be wrapped in double quotes, within which "" denotes a literal quote and CR/LF are
    // literal data. The exporter always terminates every record (including the last) with CRLF.
    // ----------------------------------------------------------------------------------------------

    private static List<List<string>> ParseRfc4180(string text)
    {
        var records = new List<List<string>>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var i = 0;
        var n = text.Length;

        while (i < n)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < n && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }

                    inQuotes = false;
                    i++;
                    continue;
                }

                field.Append(c);
                i++;
                continue;
            }

            if (c == '"')
            {
                inQuotes = true;
                i++;
                continue;
            }

            if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
                i++;
                continue;
            }

            if (c == '\r' && i + 1 < n && text[i + 1] == '\n')
            {
                fields.Add(field.ToString());
                field.Clear();
                records.Add(fields);
                fields = new List<string>();
                i += 2;
                continue;
            }

            field.Append(c);
            i++;
        }

        // Flush any unterminated final record (the exporter terminates all records, so this only
        // fires for malformed input — which would surface as a record-count mismatch in the test).
        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            records.Add(fields);
        }

        return records;
    }

    /// <summary>Renders a field with visible escapes for readable counterexample labels.</summary>
    private static string Display(string value)
        => value is null
            ? "<null>"
            : "\"" + value.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
}
