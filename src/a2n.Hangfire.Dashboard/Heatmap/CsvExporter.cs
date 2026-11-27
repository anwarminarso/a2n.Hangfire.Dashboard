using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Heatmap;

/// <summary>
/// Pure, deterministic serializer that renders a <see cref="HeatmapMatrix"/> to a self-describing
/// CSV document conforming to RFC 4180. The export is scoped by the active source, projection
/// window, viewer time zone, queue selection, and load metric supplied through the
/// <see cref="CsvExportContext"/> (Req 12.1).
/// </summary>
/// <remarks>
/// <para>The document is laid out as:</para>
/// <list type="number">
/// <item>Self-describing metadata rows identifying the active source, projection window, viewer time
/// zone, queue selection, and load metric so the exported values are interpretable (Req 12.4). Each
/// metadata row's key field is prefixed with <c>#</c> so it is visually distinct from the data
/// section while remaining a valid CSV field.</item>
/// <item>Exactly one header row: <c>queue,day,hour,value</c> (Req 12.2).</item>
/// <item>Exactly one data row per populated cell, each containing the queue, the day label, the hour
/// (0&#8211;23), and the cell's load value (Req 12.2). Rows are emitted in a deterministic order
/// (queue ascending, then day index, then hour).</item>
/// </list>
/// <para>The day dimension is labeled according to the active projection window: weekday names for
/// an <see cref="ProjectionWindowKind.IdealizedWeek"/> and calendar dates for
/// <see cref="ProjectionWindowKind.Next7Days"/> (Req 12.3). Day-label resolution can never fail the
/// export — any error falls back to a default <c>Day {index}</c> label (Req 12.7).</para>
/// <para>Every emitted field is escaped per RFC 4180: a field containing a comma, double quote, or
/// line break is wrapped in double quotes with embedded quotes doubled (Req 12.5). When the matrix
/// has no populated cells, only the metadata and header rows are emitted, with no data rows
/// (Req 12.6).</para>
/// <para>Validates portions of Requirements 12.1, 12.2, 12.3, 12.4, 12.5, 12.6, and 12.7.</para>
/// </remarks>
public static class CsvExporter
{
    /// <summary>The fixed column header row emitted before the data rows (Req 12.2).</summary>
    public const string HeaderRow = "queue,day,hour,value";

    /// <summary>RFC 4180 record separator (CRLF).</summary>
    private const string RecordSeparator = "\r\n";

    /// <summary>
    /// Serializes the supplied matrix to a self-describing RFC 4180 CSV document.
    /// </summary>
    /// <param name="matrix">The matrix whose populated cells are exported.</param>
    /// <param name="context">The contextual metadata written into the export so it is self-describing.</param>
    /// <returns>The complete CSV document as a single string.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="matrix"/> or <paramref name="context"/> is null.</exception>
    public static string Export(HeatmapMatrix matrix, CsvExportContext context)
    {
        if (matrix is null)
        {
            throw new ArgumentNullException(nameof(matrix));
        }

        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var builder = new StringBuilder();

        // 1) Self-describing metadata rows (Req 12.4).
        AppendMetadataRow(builder, "Source", DescribeSource(context.Source));
        AppendMetadataRow(builder, "Window", DescribeWindow(context.Window));
        AppendMetadataRow(builder, "Viewer time zone", DescribeTimeZone(context.ViewerTimeZoneId));
        AppendMetadataRow(builder, "Queues", DescribeQueues(context.Queues));
        AppendMetadataRow(builder, "Load metric", DescribeMetric(context.Metric));

        // 2) Header row (Req 12.2).
        builder.Append(HeaderRow).Append(RecordSeparator);

        // 3) One data row per populated cell in deterministic order (Req 12.2, 12.6).
        var window = matrix.Window;
        var orderedCells = matrix.Cells.Values
            .OrderBy(c => c.Key.Queue, StringComparer.Ordinal)
            .ThenBy(c => c.Key.DayIndex)
            .ThenBy(c => c.Key.Hour);

        foreach (var cell in orderedCells)
        {
            AppendField(builder, cell.Key.Queue);
            builder.Append(',');
            AppendField(builder, DayLabel(window, cell.Key.DayIndex));
            builder.Append(',');
            AppendField(builder, cell.Key.Hour.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
            AppendField(builder, FormatValue(cell.Value));
            builder.Append(RecordSeparator);
        }

        return builder.ToString();
    }

    private static void AppendMetadataRow(StringBuilder builder, string key, string value)
    {
        AppendField(builder, "# " + key);
        builder.Append(',');
        AppendField(builder, value);
        builder.Append(RecordSeparator);
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

    /// <summary>
    /// Produces the day label for a cell's day index according to the active window kind, never
    /// throwing: any failure falls back to a default <c>Day {index}</c> label (Req 12.3, 12.7).
    /// </summary>
    private static string DayLabel(ProjectionWindow window, int dayIndex)
    {
        try
        {
            if (window is null)
            {
                return FallbackDayLabel(dayIndex);
            }

            if (window.Kind == ProjectionWindowKind.Next7Days)
            {
                // Calendar dates anchored on the window's local start date (Req 12.3).
                var date = window.StartInclusive.Date.AddDays(dayIndex);
                return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            // Idealized week: weekday names anchored on the window's start weekday (Monday) (Req 12.3).
            var startDow = (int)window.StartInclusive.DayOfWeek;
            var dow = (DayOfWeek)(((startDow + dayIndex) % 7 + 7) % 7);
            return CultureInfo.InvariantCulture.DateTimeFormat.GetDayName(dow);
        }
        catch
        {
            // Label resolution must never fail the export (Req 12.7).
            return FallbackDayLabel(dayIndex);
        }
    }

    private static string FallbackDayLabel(int dayIndex)
        => "Day " + dayIndex.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats a load value using a culture-invariant, round-trippable representation so integral
    /// values render without a decimal point and fractional values retain full precision.
    /// </summary>
    private static string FormatValue(double value)
        => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Appends a single field to the builder, escaping it per RFC 4180: any field containing a comma,
    /// double quote, carriage return, or line feed is wrapped in double quotes with embedded double
    /// quotes doubled (Req 12.5).
    /// </summary>
    private static void AppendField(StringBuilder builder, string field)
    {
        field ??= string.Empty;

        var mustQuote = field.IndexOfAny(QuotingTriggers) >= 0;
        if (!mustQuote)
        {
            builder.Append(field);
            return;
        }

        builder.Append('"');
        foreach (var ch in field)
        {
            if (ch == '"')
            {
                builder.Append('"');
            }

            builder.Append(ch);
        }

        builder.Append('"');
    }

    /// <summary>The characters that force a field to be quoted under RFC 4180 (Req 12.5).</summary>
    private static readonly char[] QuotingTriggers = { ',', '"', '\r', '\n' };
}
