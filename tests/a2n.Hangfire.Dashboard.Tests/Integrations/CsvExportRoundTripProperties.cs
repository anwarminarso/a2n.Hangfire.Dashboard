// Feature: integrations-v2-6, Property 14: CSV export RFC 4180 round-trip
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FsCheck;
using FsCheck.Xunit;
using Xunit;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services.Export;

namespace a2n.Hangfire.Dashboard.Tests.Integrations;

/// <summary>
/// Property tests for <see cref="JobExportService.WriteCsvAsync"/>, the streamed serializer that
/// renders the current job search result set to an RFC 4180 CSV document.
///
/// **Property 14: CSV export RFC 4180 round-trip**
/// **Validates: Requirements 13.2**
///
/// For any list of job records whose string fields may contain commas, double quotes, carriage
/// returns, line feeds, or Unicode, parsing the exported CSV with an independent RFC 4180-conformant
/// reader recovers <em>exactly</em> the original field values — confirming that fields needing
/// escaping are wrapped in double quotes with embedded quotes doubled, and that nothing the exporter
/// writes is mis-parsed (Req 13.2).
///
/// The service is driven with a fake <see cref="IStorageQueryProvider"/> (implemented inline) that
/// returns the generated records page-by-page, respecting the requested page/pageSize. The output is
/// written to a <see cref="MemoryStream"/> and then decoded with a compact, self-contained RFC 4180
/// reader (no new dependency is taken). The expected field values are reconstructed independently of
/// the exporter's string-building so the assertion checks a genuine round-trip.
/// </summary>
public class CsvExportRoundTripProperties
{
    /// <summary>
    /// Building blocks for adversarial strings: ordinary text interleaved with every character class
    /// that forces RFC 4180 quoting (comma, double quote, CR, LF) plus Unicode and combinations.
    /// </summary>
    private static readonly string[] AdversarialAtoms =
    {
        "", "job", "MyApp.Jobs.Send", "default", "Succeeded", "Failed",
        ",", "\"", "\r", "\n", "\r\n", ";",
        "a,b", "say \"hi\"", "line1\nline2", "x\r\ny",
        "trailing,", "\"wrapped\"", "comma, and \"quote\"", "café", "日本語", "emoji 😀",
    };

    /// <summary>A string assembled from 0..3 adversarial atoms, so embedded specials appear anywhere.</summary>
    private static Gen<string> AdversarialStringGen =>
        from n in Gen.Choose(0, 3)
        from atoms in Gen.ArrayOf(n, Gen.Elements(AdversarialAtoms))
        select string.Concat(atoms);

    /// <summary>A nullable adversarial string (the string fields on <see cref="JobSummaryDto"/> may be null).</summary>
    private static Gen<string> NullableAdversarialStringGen =>
        Gen.Frequency(
            Tuple.Create(1, Gen.Constant<string>(null)),
            Tuple.Create(6, AdversarialStringGen));

    /// <summary>A nullable UTC <see cref="DateTime"/> spanning a realistic range (or null).</summary>
    private static Gen<DateTime?> NullableDateGen =>
        Gen.Frequency(
            Tuple.Create(1, Gen.Constant<DateTime?>(null)),
            Tuple.Create(4,
                from dayOffset in Gen.Choose(0, 4000)
                from ms in Gen.Choose(0, 86_400_000 - 1)
                select (DateTime?)new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    .AddDays(dayOffset).AddMilliseconds(ms)));

    /// <summary>A nullable duration/latency value in milliseconds (integral and fractional), or null.</summary>
    private static Gen<double?> NullableMillisGen =>
        Gen.Frequency(
            Tuple.Create(1, Gen.Constant<double?>(null)),
            Tuple.Create(4,
                from hundredths in Gen.Choose(0, 100_000_00)
                select (double?)(hundredths / 100d)));

    /// <summary>A possibly-null tag array whose entries may be null or adversarial.</summary>
    private static Gen<string[]> TagsGen =>
        Gen.Frequency(
            Tuple.Create(1, Gen.Constant<string[]>(null)),
            Tuple.Create(1, Gen.Constant(Array.Empty<string>())),
            Tuple.Create(5,
                from n in Gen.Choose(1, 4)
                from tags in Gen.ArrayOf(n, AdversarialStringGen)
                select tags));

    private static Gen<JobSummaryDto> JobGen =>
        from jobId in AdversarialStringGen
        from jobName in NullableAdversarialStringGen
        from state in NullableAdversarialStringGen
        from queue in NullableAdversarialStringGen
        from createdAt in NullableDateGen
        from lastChange in NullableDateGen
        from duration in NullableMillisGen
        from latency in NullableMillisGen
        from tags in TagsGen
        from exType in NullableAdversarialStringGen
        from exMsg in NullableAdversarialStringGen
        select new JobSummaryDto
        {
            JobId = jobId,
            JobName = jobName,
            State = state,
            Queue = queue,
            CreatedAt = createdAt,
            LastStateChange = lastChange,
            DurationMs = duration,
            LatencyMs = latency,
            Tags = tags,
            ExceptionType = exType,
            ExceptionMessage = exMsg,
        };

    /// <summary>
    /// **Property 14: CSV export RFC 4180 round-trip**
    /// **Validates: Requirements 13.2**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property CsvExport_RoundTripsThrough_Rfc4180Parser()
    {
        var arb = Arb.From(
            from count in Gen.Choose(0, 60)
            from jobs in Gen.ArrayOf(count, JobGen)
            select jobs);

        return Prop.ForAll(arb, jobs =>
        {
            var provider = new FakeQueryProvider(jobs);
            var service = new JobExportService(provider);

            using var buffer = new MemoryStream();
            service
                .WriteCsvAsync(buffer, new JobFilterCriteria(), maxRecords: 1_000_000, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var csv = new UTF8Encoding(false).GetString(buffer.ToArray());
            var parsed = ParseRfc4180(csv);
            var expected = ExpectedRecords(jobs);

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
    /// Anchor example: a job name and exception message containing commas, double quotes, and a CRLF
    /// round-trip exactly through the parser, landing as single fields rather than being split on
    /// their embedded commas or newlines (Req 13.2).
    /// </summary>
    [Fact]
    public void AdversarialStringFields_RoundTrip_AsSingleFields()
    {
        const string nastyName = "MyApp.Send(\"a,b\")\r\nOverload";
        const string nastyMessage = "boom: \"quoted\", line\nbreak";

        var job = new JobSummaryDto
        {
            JobId = "id-1",
            JobName = nastyName,
            State = "Failed",
            Queue = "default",
            CreatedAt = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            LastStateChange = new DateTime(2024, 1, 2, 3, 5, 6, DateTimeKind.Utc),
            DurationMs = 12.5d,
            LatencyMs = null,
            Tags = new[] { "alpha", "be,ta" },
            ExceptionType = "System.InvalidOperationException",
            ExceptionMessage = nastyMessage,
        };

        var service = new JobExportService(new FakeQueryProvider(new[] { job }));

        using var buffer = new MemoryStream();
        service
            .WriteCsvAsync(buffer, new JobFilterCriteria(), maxRecords: 10, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var csv = new UTF8Encoding(false).GetString(buffer.ToArray());
        var parsed = ParseRfc4180(csv);

        Assert.Equal(2, parsed.Count); // header + one data row
        var dataRow = parsed[1];
        Assert.Equal(11, dataRow.Count);
        Assert.Equal("id-1", dataRow[0]);
        Assert.Equal(nastyName, dataRow[1]);
        Assert.Equal("Failed", dataRow[2]);
        Assert.Equal("default", dataRow[3]);
        Assert.Equal("alpha;be,ta", dataRow[8]);
        Assert.Equal("System.InvalidOperationException", dataRow[9]);
        Assert.Equal(nastyMessage, dataRow[10]);
    }

    // ----------------------------------------------------------------------------------------------
    // Independent oracle: the exact field values the exporter is contractually required to write.
    // Column order (per JobExportService.HeaderRow):
    // JobId, JobName, State, Queue, CreatedAt, LastStateChange, DurationMs, LatencyMs, Tags,
    // ExceptionType, ExceptionMessage
    // ----------------------------------------------------------------------------------------------

    private static List<List<string>> ExpectedRecords(IReadOnlyList<JobSummaryDto> jobs)
    {
        var records = new List<List<string>>
        {
            new()
            {
                "JobId", "JobName", "State", "Queue", "CreatedAt", "LastStateChange",
                "DurationMs", "LatencyMs", "Tags", "ExceptionType", "ExceptionMessage",
            },
        };

        foreach (var job in jobs)
        {
            records.Add(new List<string>
            {
                job.JobId ?? string.Empty,
                job.JobName ?? string.Empty,
                job.State ?? string.Empty,
                job.Queue ?? string.Empty,
                FormatDate(job.CreatedAt),
                FormatDate(job.LastStateChange),
                FormatNumber(job.DurationMs),
                FormatNumber(job.LatencyMs),
                FormatTags(job.Tags),
                job.ExceptionType ?? string.Empty,
                job.ExceptionMessage ?? string.Empty,
            });
        }

        return records;
    }

    private static string FormatDate(DateTime? value)
        => value.HasValue ? value.Value.ToString("o", CultureInfo.InvariantCulture) : string.Empty;

    private static string FormatNumber(double? value)
        => value.HasValue ? value.Value.ToString("R", CultureInfo.InvariantCulture) : string.Empty;

    private static string FormatTags(string[] tags)
        => tags is null || tags.Length == 0 ? string.Empty : string.Join(";", tags);

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

    /// <summary>
    /// A fake <see cref="IStorageQueryProvider"/> that pages through a fixed record set, honoring the
    /// requested 1-based <c>page</c> and <c>pageSize</c> so the service's paging loop is driven end to
    /// end. Only <see cref="GetJobsWithFilterAsync"/> is exercised by the CSV export.
    /// </summary>
    private sealed class FakeQueryProvider : IStorageQueryProvider
    {
        private readonly IReadOnlyList<JobSummaryDto> _all;

        public FakeQueryProvider(IReadOnlyList<JobSummaryDto> all) => _all = all;

        public Task<PagedResult<JobSummaryDto>> GetJobsWithFilterAsync(
            JobFilterCriteria criteria, int page, int pageSize, CancellationToken ct)
        {
            var skip = Math.Max(0, (page - 1) * pageSize);
            var items = _all.Skip(skip).Take(pageSize).ToList();
            return Task.FromResult(new PagedResult<JobSummaryDto>
            {
                Items = items,
                TotalCount = _all.Count,
                Page = page,
                PageSize = pageSize,
            });
        }

        public Task<PagedResult<JobSummaryDto>> GetJobsByTagAsync(
            string tag, int page, int pageSize, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TagCountDto>> GetTagCloudAsync(CancellationToken ct)
            => throw new NotSupportedException();

        public Task<PagedResult<JobSummaryDto>> GetJobsByStateAsync(
            string stateName, int page, int pageSize, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SlowestJobDto>> GetSlowestJobsAsync(
            int count, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
