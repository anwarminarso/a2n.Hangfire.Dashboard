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
/// Property tests for the structural layout of <see cref="CsvExporter.Export"/>.
///
/// **Property 23: CSV structure matches the matrix**
/// **Validates: Requirements 12.2, 12.3, 12.6**
///
/// For any matrix, the export emits exactly one header row followed by exactly one data row per
/// populated cell, each data row carrying that cell's queue, day, hour (0–23), and load value; the
/// day dimension is labeled with weekday names for the Idealized-week window and calendar dates for
/// the Next-7-days window; and an empty matrix yields the header and metadata only with no data rows.
/// </summary>
public class CsvExporterStructureProperties
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Queue names drawn from a pool that includes plain names plus names containing the RFC 4180
    /// quoting triggers (comma, double quote, line break) so the structural parse is exercised
    /// against escaped fields, not just simple ones.
    /// </summary>
    private static readonly string[] QueuePool =
    {
        "default",
        "critical",
        "emails",
        "alpha,beta",       // embedded comma
        "say \"hello\"",    // embedded double quotes
        "line\r\nbreak"     // embedded line break
    };

    private static Gen<string> QueueGen => Gen.Elements(QueuePool);

    private static Gen<ProjectionWindowKind> WindowKindGen =>
        Gen.Elements(ProjectionWindowKind.IdealizedWeek, ProjectionWindowKind.Next7Days);

    /// <summary>
    /// A window start instant spread across many calendar dates (and therefore every weekday) so the
    /// day-label resolution is exercised for both window kinds and all seven start weekdays.
    /// </summary>
    private static Gen<DateTimeOffset> StartGen =>
        from dayOffset in Gen.Choose(0, 3000)
        from hour in Gen.Choose(0, 23)
        select new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(dayOffset).AddHours(hour);

    /// <summary>A finite, non-negative load value mixing integral and fractional magnitudes.</summary>
    private static Gen<double> ValueGen =>
        from whole in Gen.Choose(0, 100_000)
        from milli in Gen.Choose(0, 999)
        select whole + (milli / 1000.0);

    private static Gen<(string Queue, int Day, int Hour, double Value)> CellTupleGen =>
        from q in QueueGen
        from day in Gen.Choose(0, 6)
        from hour in Gen.Choose(0, 23)
        from v in ValueGen
        select (q, day, hour, v);

    /// <summary>
    /// A matrix over a random window (kind + start) with a variable number of populated cells,
    /// including the empty matrix (Req 12.6). Duplicate <c>queue × day × hour</c> addresses collapse
    /// to a single cell, matching the matrix invariant.
    /// </summary>
    private static Gen<HeatmapMatrix> MatrixGen =>
        from kind in WindowKindGen
        from start in StartGen
        from metric in Gen.Elements(LoadMetric.FireCount, LoadMetric.WorkerMinutes)
        from rawCells in Gen.ListOf(CellTupleGen)
        select BuildMatrix(kind, start, metric, rawCells);

    private static HeatmapMatrix BuildMatrix(
        ProjectionWindowKind kind,
        DateTimeOffset start,
        LoadMetric metric,
        IEnumerable<(string Queue, int Day, int Hour, double Value)> rawCells)
    {
        var window = new ProjectionWindow(start, start.AddDays(7), kind);
        var cells = new Dictionary<CellKey, HeatmapCell>();
        foreach (var (q, day, hour, value) in rawCells)
        {
            var key = new CellKey(q, day, hour);
            cells[key] = new HeatmapCell(key, value, 1, q, new[] { "job-" + q });
        }

        var queues = cells.Keys
            .Select(k => k.Queue)
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var min = cells.Count == 0 ? 0d : cells.Values.Min(c => c.Value);
        var max = cells.Count == 0 ? 0d : cells.Values.Max(c => c.Value);

        return new HeatmapMatrix(cells, queues, window, metric, min, max);
    }

    /// <summary>
    /// The day label the exporter is expected to produce for a cell's day index under the active
    /// window kind: weekday names for an Idealized week, calendar dates for Next 7 days (Req 12.3).
    /// This mirrors the exporter's own (never-throwing) labeling so the property checks the contract,
    /// not the implementation details.
    /// </summary>
    private static string ExpectedDayLabel(ProjectionWindow window, int dayIndex)
    {
        if (window.Kind == ProjectionWindowKind.Next7Days)
        {
            return window.StartInclusive.Date.AddDays(dayIndex).ToString("yyyy-MM-dd", Inv);
        }

        var startDow = (int)window.StartInclusive.DayOfWeek;
        var dow = (DayOfWeek)((((startDow + dayIndex) % 7) + 7) % 7);
        return Inv.DateTimeFormat.GetDayName(dow);
    }

    /// <summary>
    /// **Property 23: CSV structure matches the matrix**
    /// **Validates: Requirements 12.2, 12.3, 12.6**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property CsvStructure_MatchesTheMatrix()
    {
        return Prop.ForAll(Arb.From(MatrixGen), matrix =>
        {
            var context = new CsvExportContext(
                HeatmapSource.Projected,
                matrix.Window,
                "UTC",
                matrix.Queues,
                matrix.Metric);

            var csv = CsvExporter.Export(matrix, context);
            var records = ParseCsv(csv);

            // Locate the single header row. Metadata rows precede it; data rows follow it.
            var headerIndex = records.FindIndex(r =>
                r.Count == 4 && r[0] == "queue" && r[1] == "day" && r[2] == "hour" && r[3] == "value");

            if (headerIndex < 0)
            {
                return false.Label("no 'queue,day,hour,value' header row was emitted");
            }

            // Exactly one header row (no other record matches the header signature).
            var headerCount = records.Count(r =>
                r.Count == 4 && r[0] == "queue" && r[1] == "day" && r[2] == "hour" && r[3] == "value");
            if (headerCount != 1)
            {
                return false.Label($"expected exactly one header row, found {headerCount}");
            }

            // Everything before the header is self-describing metadata (each key prefixed with '#').
            var metadataRows = records.Take(headerIndex).ToList();
            if (metadataRows.Any(r => r.Count == 0 || !r[0].StartsWith("#", StringComparison.Ordinal)))
            {
                return false.Label("a pre-header (metadata) row was not prefixed with '#'");
            }

            var dataRows = records.Skip(headerIndex + 1).ToList();

            // --- Req 12.6: an empty matrix produces no data rows (metadata + header only). ---
            if (matrix.Cells.Count == 0 && dataRows.Count != 0)
            {
                return false.Label($"empty matrix produced {dataRows.Count} data row(s); expected none");
            }

            // --- Req 12.2: exactly one data row per populated cell. ---
            if (dataRows.Count != matrix.Cells.Count)
            {
                return false.Label(
                    $"data-row count {dataRows.Count} != populated-cell count {matrix.Cells.Count}");
            }

            // Each data row carries exactly four fields: queue, day, hour, value (Req 12.2).
            if (dataRows.Any(r => r.Count != 4))
            {
                return false.Label("a data row did not have exactly 4 fields (queue,day,hour,value)");
            }

            // --- Req 12.2 + 12.3: the set of data rows matches the matrix cells, with the day field
            // labeled per the active window kind. Compare as ordered multisets of
            // (queue, dayLabel, hour, value). ---
            var expected = matrix.Cells.Values
                .Select(c => (
                    Queue: c.Key.Queue,
                    Day: ExpectedDayLabel(matrix.Window, c.Key.DayIndex),
                    Hour: c.Key.Hour,
                    Value: c.Value))
                .OrderBy(t => t.Queue, StringComparer.Ordinal)
                .ThenBy(t => t.Day, StringComparer.Ordinal)
                .ThenBy(t => t.Hour)
                .ThenBy(t => t.Value)
                .ToList();

            List<(string Queue, string Day, int Hour, double Value)> actual;
            try
            {
                actual = dataRows
                    .Select(r => (
                        Queue: r[0],
                        Day: r[1],
                        Hour: int.Parse(r[2], Inv),
                        Value: double.Parse(r[3], NumberStyles.Float, Inv)))
                    .OrderBy(t => t.Queue, StringComparer.Ordinal)
                    .ThenBy(t => t.Day, StringComparer.Ordinal)
                    .ThenBy(t => t.Hour)
                    .ThenBy(t => t.Value)
                    .ToList();
            }
            catch (FormatException)
            {
                return false.Label("a data row's hour or value field was not a parseable number");
            }

            for (var i = 0; i < expected.Count; i++)
            {
                var e = expected[i];
                var a = actual[i];

                if (!string.Equals(e.Queue, a.Queue, StringComparison.Ordinal))
                {
                    return false.Label($"queue mismatch: expected '{e.Queue}', got '{a.Queue}'");
                }

                // Day label must match the expected weekday/date for the active window kind (Req 12.3).
                if (!string.Equals(e.Day, a.Day, StringComparison.Ordinal))
                {
                    return false.Label(
                        $"day label mismatch for {matrix.Window.Kind}: expected '{e.Day}', got '{a.Day}'");
                }

                // Hour must lie in 0..23 and equal the cell's hour (Req 12.2).
                if (a.Hour < 0 || a.Hour > 23 || a.Hour != e.Hour)
                {
                    return false.Label($"hour mismatch: expected {e.Hour}, got {a.Hour}");
                }

                if (a.Value != e.Value)
                {
                    return false.Label($"value mismatch: expected {e.Value}, got {a.Value}");
                }
            }

            return true.ToProperty();
        });
    }

    /// <summary>
    /// A minimal RFC 4180-aware parser: splits the document into records and fields, honoring
    /// double-quoted fields (which may contain commas, line breaks, and doubled embedded quotes).
    /// Records are separated by CRLF (the separator the exporter emits); a CR or LF on its own is
    /// also accepted as a record terminator for robustness.
    /// </summary>
    private static List<List<string>> ParseCsv(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var i = 0;

        void EndField() { record.Add(field.ToString()); field.Clear(); }
        void EndRecord() { EndField(); records.Add(record); record = new List<string>(); }

        while (i < text.Length)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                    }
                    else
                    {
                        inQuotes = false;
                        i++;
                    }
                }
                else
                {
                    field.Append(c);
                    i++;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    i++;
                    break;
                case ',':
                    EndField();
                    i++;
                    break;
                case '\r':
                    EndRecord();
                    i += (i + 1 < text.Length && text[i + 1] == '\n') ? 2 : 1;
                    break;
                case '\n':
                    EndRecord();
                    i++;
                    break;
                default:
                    field.Append(c);
                    i++;
                    break;
            }
        }

        // Flush any trailing partial record (none expected, since every emitted row ends with CRLF).
        if (field.Length > 0 || record.Count > 0)
        {
            EndRecord();
        }

        return records;
    }
}
