#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Services.Export;

/// <summary>
/// Streams the current job search result set to a response body as CSV or JSON.
/// <para>
/// Records are pulled page-by-page from <see cref="IStorageQueryProvider.GetJobsWithFilterAsync"/>
/// using the currently applied <see cref="JobFilterCriteria"/> (the dashboard's
/// <c>Search_Criteria</c>) and each page is written directly to the output stream, so memory use
/// does not grow with the total number of exported records (Req 13.1, 13.4, 13.6).
/// </para>
/// <list type="bullet">
/// <item><description><b>CSV</b> — RFC 4180 conformant: a field containing a comma, double quote,
/// carriage return, or line feed is wrapped in double quotes with embedded quotes doubled, matching
/// the escaping proven in <see cref="Heatmap.CsvExporter"/> (Req 13.2).</description></item>
/// <item><description><b>JSON</b> — a streamed JSON array of <see cref="JobRecordDto"/> (the shared
/// record shape projected via <see cref="JobRecordProjection.ToRecord"/>), written with a
/// <see cref="Utf8JsonWriter"/> over the response stream (Req 13.3).</description></item>
/// </list>
/// <para>
/// Both writers stop once <c>maxRecords</c> (from <see cref="ExportOptions.MaxRecords"/>) records
/// have been written.
/// </para>
/// </summary>
public sealed class JobExportService
{
    /// <summary>
    /// Number of records fetched per <see cref="IStorageQueryProvider.GetJobsWithFilterAsync"/> call.
    /// Bounds per-call memory while keeping the number of round trips reasonable.
    /// </summary>
    private const int PageSize = 500;

    /// <summary>RFC 4180 record separator (CRLF), matching <see cref="Heatmap.CsvExporter"/>.</summary>
    private const string RecordSeparator = "\r\n";

    /// <summary>The characters that force a CSV field to be quoted under RFC 4180 (Req 13.2).</summary>
    private static readonly char[] QuotingTriggers = { ',', '"', '\r', '\n' };

    /// <summary>The fixed CSV header row emitted before the data rows.</summary>
    private const string HeaderRow =
        "JobId,JobName,State,Queue,CreatedAt,LastStateChange,DurationMs,LatencyMs,Tags,ExceptionType,ExceptionMessage";

    private readonly IStorageQueryProvider _queryProvider;

    /// <summary>
    /// Creates the export service. The <see cref="IStorageQueryProvider"/> is supplied via
    /// constructor DI, matching sibling services such as <c>SearchService</c>.
    /// </summary>
    /// <param name="queryProvider">The storage query provider used to page through job records.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="queryProvider"/> is <c>null</c>.</exception>
    public JobExportService(IStorageQueryProvider queryProvider)
    {
        _queryProvider = queryProvider ?? throw new ArgumentNullException(nameof(queryProvider));
    }

    /// <summary>
    /// Streams the jobs matching <paramref name="criteria"/> as an RFC 4180 CSV document to
    /// <paramref name="output"/>, stopping after at most <paramref name="maxRecords"/> records.
    /// </summary>
    /// <param name="output">The response body stream to write to.</param>
    /// <param name="criteria">The current search criteria to export.</param>
    /// <param name="maxRecords">The maximum number of records to write.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteCsvAsync(Stream output, JobFilterCriteria criteria, int maxRecords, CancellationToken ct)
    {
        if (output is null)
            throw new ArgumentNullException(nameof(output));
        if (criteria is null)
            throw new ArgumentNullException(nameof(criteria));

        // leaveOpen: true so the caller owns the lifetime of the response stream.
        await using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1 << 14, leaveOpen: true);

        var row = new StringBuilder(256);
        await writer.WriteAsync(HeaderRow).ConfigureAwait(false);
        await writer.WriteAsync(RecordSeparator).ConfigureAwait(false);

        await foreach (var job in EnumerateAsync(criteria, maxRecords, ct).ConfigureAwait(false))
        {
            row.Clear();
            AppendField(row, job.JobId);
            row.Append(',');
            AppendField(row, job.JobName);
            row.Append(',');
            AppendField(row, job.State);
            row.Append(',');
            AppendField(row, job.Queue);
            row.Append(',');
            AppendField(row, FormatDate(job.CreatedAt));
            row.Append(',');
            AppendField(row, FormatDate(job.LastStateChange));
            row.Append(',');
            AppendField(row, FormatNumber(job.DurationMs));
            row.Append(',');
            AppendField(row, FormatNumber(job.LatencyMs));
            row.Append(',');
            AppendField(row, FormatTags(job.Tags));
            row.Append(',');
            AppendField(row, job.ExceptionType);
            row.Append(',');
            AppendField(row, job.ExceptionMessage);
            row.Append(RecordSeparator);

            await writer.WriteAsync(row.ToString()).ConfigureAwait(false);
        }

        await writer.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Streams the jobs matching <paramref name="criteria"/> as a JSON array of
    /// <see cref="JobRecordDto"/> to <paramref name="output"/>, stopping after at most
    /// <paramref name="maxRecords"/> records.
    /// </summary>
    /// <param name="output">The response body stream to write to.</param>
    /// <param name="criteria">The current search criteria to export.</param>
    /// <param name="maxRecords">The maximum number of records to write.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteJsonAsync(Stream output, JobFilterCriteria criteria, int maxRecords, CancellationToken ct)
    {
        if (output is null)
            throw new ArgumentNullException(nameof(output));
        if (criteria is null)
            throw new ArgumentNullException(nameof(criteria));

        await using var jsonWriter = new Utf8JsonWriter(output, new JsonWriterOptions { SkipValidation = true });

        jsonWriter.WriteStartArray();

        var flushEvery = 0;
        await foreach (var job in EnumerateAsync(criteria, maxRecords, ct).ConfigureAwait(false))
        {
            JsonSerializer.Serialize(jsonWriter, job.ToRecord(), JsonOptions);

            // Periodically flush so buffered JSON is pushed to the network and memory stays bounded.
            if (++flushEvery >= PageSize)
            {
                await jsonWriter.FlushAsync(ct).ConfigureAwait(false);
                flushEvery = 0;
            }
        }

        jsonWriter.WriteEndArray();
        await jsonWriter.FlushAsync(ct).ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Pages through the query provider for <paramref name="criteria"/>, yielding at most
    /// <paramref name="maxRecords"/> job summaries in provider order without accumulating all
    /// records in memory (Req 13.4).
    /// </summary>
    private async IAsyncEnumerable<JobSummaryDto> EnumerateAsync(
        JobFilterCriteria criteria,
        int maxRecords,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (maxRecords <= 0)
            yield break;

        var written = 0;
        var page = 1;

        while (written < maxRecords)
        {
            ct.ThrowIfCancellationRequested();

            var result = await _queryProvider
                .GetJobsWithFilterAsync(criteria, page, PageSize, ct)
                .ConfigureAwait(false);

            var items = result?.Items;
            if (items is null || items.Count == 0)
                yield break;

            foreach (var item in items)
            {
                if (item is null)
                    continue;

                yield return item;

                if (++written >= maxRecords)
                    yield break;
            }

            // Last page reached when fewer than a full page was returned.
            if (items.Count < PageSize)
                yield break;

            page++;
        }
    }

    /// <summary>
    /// Appends a single field to <paramref name="builder"/>, escaping it per RFC 4180: any field
    /// containing a comma, double quote, carriage return, or line feed is wrapped in double quotes
    /// with embedded double quotes doubled (Req 13.2). Mirrors <see cref="Heatmap.CsvExporter"/>.
    /// </summary>
    private static void AppendField(StringBuilder builder, string? field)
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
                builder.Append('"');

            builder.Append(ch);
        }

        builder.Append('"');
    }

    private static string FormatDate(DateTime? value)
        => value.HasValue ? value.Value.ToString("o", CultureInfo.InvariantCulture) : string.Empty;

    private static string FormatNumber(double? value)
        => value.HasValue ? value.Value.ToString("R", CultureInfo.InvariantCulture) : string.Empty;

    private static string FormatTags(string[]? tags)
        => tags is null || tags.Length == 0 ? string.Empty : string.Join(";", tags);
}
