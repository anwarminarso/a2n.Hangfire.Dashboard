using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
/// Property tests for the JSON export path of
/// <see cref="JobExportService.WriteJsonAsync(Stream, JobFilterCriteria, int, CancellationToken)"/>.
///
/// Feature: integrations-v2-6, Property 15: JSON export round-trip and shared shape
///
/// **Property 15: JSON export round-trip and shared shape** — for any list of job records, the
/// service writes a JSON array which, when deserialized with the Web JSON defaults into
/// <c>List&lt;JobRecordDto&gt;</c> (the same record shape the REST API exposes), yields a list equal
/// to the projected input (same shape, same field values, in order). The service is driven with a
/// fake <see cref="IStorageQueryProvider"/> that returns the generated <see cref="JobSummaryDto"/>
/// records as pages; the expected list is obtained by projecting each generated summary via
/// <see cref="JobRecordProjection.ToRecord"/>.
///
/// <see cref="JobExportService"/> serializes with <c>new JsonSerializerOptions(JsonSerializerDefaults.Web)</c>,
/// so the round-trip deserializes with the matching options.
///
/// **Validates: Requirements 13.3**
/// </summary>
public class JsonExportRoundTripProperties
{
    /// <summary>The exact serializer options the service uses, mirrored for the deserialize side.</summary>
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A fake <see cref="IStorageQueryProvider"/> that serves a fixed list of
    /// <see cref="JobSummaryDto"/> records in fixed order, paged by the requested page/pageSize, so
    /// <see cref="JobExportService"/> exercises its real paging loop without a database.
    /// Only <see cref="GetJobsWithFilterAsync"/> is used by the export path; the rest throw to make
    /// any unexpected dependency obvious.
    /// </summary>
    private sealed class FakeQueryProvider : IStorageQueryProvider
    {
        private readonly IReadOnlyList<JobSummaryDto> _all;

        public FakeQueryProvider(IReadOnlyList<JobSummaryDto> all) => _all = all;

        public Task<PagedResult<JobSummaryDto>> GetJobsWithFilterAsync(
            JobFilterCriteria criteria, int page, int pageSize, CancellationToken ct)
        {
            var skip = (page - 1) * pageSize;
            var items = _all.Skip(skip).Take(pageSize).ToList();

            return Task.FromResult(new PagedResult<JobSummaryDto>
            {
                Items = items,
                TotalCount = _all.Count,
                Page = page,
                PageSize = pageSize
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

    /// <summary>
    /// Adversarial string atoms: ordinary text plus specials and Unicode that stress JSON
    /// escaping (quotes, backslashes, control chars, newlines, non-ASCII).
    /// </summary>
    private static readonly string[] StringAtoms =
    {
        "", "job", "Namespace.Type.Method", "default", "Succeeded", "Failed",
        "with space", "with,comma", "with\"quote", "back\\slash",
        "line1\nline2", "tab\ttab", "carriage\rreturn",
        "café", "ünïcödé", "日本語", "emoji😀", "\u0001\u001f", "</script>",
    };

    private static Gen<string> StringGen =>
        from n in Gen.Choose(0, 3)
        from atoms in Gen.ArrayOf(n, Gen.Elements(StringAtoms))
        select string.Concat(atoms);

    private static Gen<string> NullableStringGen =>
        Gen.Frequency(
            Tuple.Create(1, Gen.Constant<string>(null)),
            Tuple.Create(5, StringGen));

    private static Gen<DateTime?> NullableDateGen =>
        Gen.Frequency(
            Tuple.Create(1, Gen.Constant<DateTime?>(null)),
            Tuple.Create(4,
                from ticks in Gen.Choose(0, 4000)
                // Anchor on a real date; keep to UTC so serialization is deterministic.
                select (DateTime?)new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(ticks)));

    private static Gen<double?> NullableNumberGen =>
        Gen.Frequency(
            Tuple.Create(1, Gen.Constant<double?>(null)),
            Tuple.Create(4,
                from hundredths in Gen.Choose(0, 10_000_00)
                select (double?)(hundredths / 100d)));

    private static Gen<string[]> TagsGen =>
        Gen.Frequency(
            Tuple.Create(1, Gen.Constant<string[]>(null)),
            Tuple.Create(1, Gen.Constant(Array.Empty<string>())),
            Tuple.Create(4,
                from n in Gen.Choose(1, 4)
                from tags in Gen.ArrayOf(n, StringGen)
                select tags));

    private static Gen<JobSummaryDto> SummaryGen =>
        from jobId in StringGen
        from jobName in StringGen
        from state in StringGen
        from queue in StringGen
        from createdAt in NullableDateGen
        from lastStateChange in NullableDateGen
        from durationMs in NullableNumberGen
        from latencyMs in NullableNumberGen
        from tags in TagsGen
        from exType in NullableStringGen
        from exMsg in NullableStringGen
        select new JobSummaryDto
        {
            JobId = jobId,
            JobName = jobName,
            State = state,
            Queue = queue,
            CreatedAt = createdAt,
            LastStateChange = lastStateChange,
            DurationMs = durationMs,
            LatencyMs = latencyMs,
            Tags = tags,
            ExceptionType = exType,
            ExceptionMessage = exMsg
        };

    private static Gen<List<JobSummaryDto>> SummaryListGen =>
        from n in Gen.Choose(0, 30)
        from items in Gen.ArrayOf(n, SummaryGen)
        select items.ToList();

    /// <summary>
    /// **Property 15: JSON export round-trip and shared shape**
    /// **Validates: Requirements 13.3**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property JsonExport_RoundTrips_AsSharedJobRecordShape()
    {
        return Prop.ForAll(Arb.From(SummaryListGen), summaries =>
        {
            var provider = new FakeQueryProvider(summaries);
            var service = new JobExportService(provider);

            // Use a maxRecords large enough to include every generated record.
            var maxRecords = summaries.Count + 10;

            using var stream = new MemoryStream();
            service
                .WriteJsonAsync(stream, new JobFilterCriteria(), maxRecords, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var deserialized = JsonSerializer.Deserialize<List<JobRecordDto>>(
                stream.ToArray(), WebOptions);

            // The expected list is the shared REST API record shape projected from each summary.
            var expected = summaries.Select(s => s.ToRecord()).ToList();

            if (deserialized is null)
            {
                return false.Label("deserialized list was null");
            }

            if (deserialized.Count != expected.Count)
            {
                return false.Label(
                    $"count mismatch: deserialized={deserialized.Count} expected={expected.Count}");
            }

            for (var i = 0; i < expected.Count; i++)
            {
                if (!RecordsEqual(expected[i], deserialized[i], out var reason))
                {
                    return false.Label($"record {i} did not round-trip: {reason}");
                }
            }

            return true.ToProperty();
        });
    }

    /// <summary>
    /// Anchor example: a single record whose every string field carries JSON-hostile characters and
    /// whose tags contain specials survives the write/deserialize round-trip exactly.
    /// </summary>
    [Fact]
    public void AdversarialRecord_RoundTrips_Exactly()
    {
        var summary = new JobSummaryDto
        {
            JobId = "id\"with\\quote",
            JobName = "Ns.Type.Method(\"arg\")",
            State = "Failed",
            Queue = "queue,with\nnewline",
            CreatedAt = new DateTime(2024, 6, 1, 12, 30, 0, DateTimeKind.Utc),
            LastStateChange = null,
            DurationMs = 1234.56,
            LatencyMs = null,
            Tags = new[] { "tag,1", "tag\"2", "日本語" },
            ExceptionType = "System.InvalidOperationException",
            ExceptionMessage = "boom\r\nline2 café"
        };

        var provider = new FakeQueryProvider(new[] { summary });
        var service = new JobExportService(provider);

        using var stream = new MemoryStream();
        service
            .WriteJsonAsync(stream, new JobFilterCriteria(), 100, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var deserialized = JsonSerializer.Deserialize<List<JobRecordDto>>(stream.ToArray(), WebOptions);

        Assert.NotNull(deserialized);
        var single = Assert.Single(deserialized);
        Assert.True(RecordsEqual(summary.ToRecord(), single, out var reason), reason);
    }

    /// <summary>
    /// Field-wise equality for <see cref="JobRecordDto"/>. Record value-equality treats
    /// <c>Tags</c> (a <c>string[]</c>) by reference, so the array is compared element-wise here while
    /// every other field is compared directly.
    /// </summary>
    private static bool RecordsEqual(JobRecordDto expected, JobRecordDto actual, out string reason)
    {
        if (!string.Equals(expected.JobId, actual.JobId, StringComparison.Ordinal))
        {
            reason = $"JobId: expected {Display(expected.JobId)} actual {Display(actual.JobId)}";
            return false;
        }

        if (!string.Equals(expected.JobName, actual.JobName, StringComparison.Ordinal))
        {
            reason = $"JobName: expected {Display(expected.JobName)} actual {Display(actual.JobName)}";
            return false;
        }

        if (!string.Equals(expected.State, actual.State, StringComparison.Ordinal))
        {
            reason = $"State: expected {Display(expected.State)} actual {Display(actual.State)}";
            return false;
        }

        if (!string.Equals(expected.Queue, actual.Queue, StringComparison.Ordinal))
        {
            reason = $"Queue: expected {Display(expected.Queue)} actual {Display(actual.Queue)}";
            return false;
        }

        if (expected.CreatedAt != actual.CreatedAt)
        {
            reason = $"CreatedAt: expected {expected.CreatedAt:o} actual {actual.CreatedAt:o}";
            return false;
        }

        if (expected.LastStateChange != actual.LastStateChange)
        {
            reason = $"LastStateChange: expected {expected.LastStateChange:o} actual {actual.LastStateChange:o}";
            return false;
        }

        if (expected.DurationMs != actual.DurationMs)
        {
            reason = $"DurationMs: expected {expected.DurationMs} actual {actual.DurationMs}";
            return false;
        }

        if (expected.LatencyMs != actual.LatencyMs)
        {
            reason = $"LatencyMs: expected {expected.LatencyMs} actual {actual.LatencyMs}";
            return false;
        }

        if (!string.Equals(expected.ExceptionType, actual.ExceptionType, StringComparison.Ordinal))
        {
            reason = $"ExceptionType: expected {Display(expected.ExceptionType)} actual {Display(actual.ExceptionType)}";
            return false;
        }

        if (!string.Equals(expected.ExceptionMessage, actual.ExceptionMessage, StringComparison.Ordinal))
        {
            reason = $"ExceptionMessage: expected {Display(expected.ExceptionMessage)} actual {Display(actual.ExceptionMessage)}";
            return false;
        }

        if (!TagsEqual(expected.Tags, actual.Tags))
        {
            reason =
                $"Tags: expected [{DescribeTags(expected.Tags)}] actual [{DescribeTags(actual.Tags)}]";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Compares two tag arrays element-wise. A <c>null</c> array and an empty array are treated as
    /// equivalent because the shared shape does not distinguish "no tags" representations across the
    /// JSON boundary.
    /// </summary>
    private static bool TagsEqual(string[] expected, string[] actual)
    {
        var e = expected ?? Array.Empty<string>();
        var a = actual ?? Array.Empty<string>();
        return e.SequenceEqual(a, StringComparer.Ordinal);
    }

    private static string DescribeTags(string[] tags)
        => tags is null ? "<null>" : string.Join(", ", tags.Select(Display));

    private static string Display(string value)
        => value is null
            ? "<null>"
            : "\"" + value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
}
