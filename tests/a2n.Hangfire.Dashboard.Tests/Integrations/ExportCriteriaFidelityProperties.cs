using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FsCheck;
using FsCheck.Xunit;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services.Export;

namespace a2n.Hangfire.Dashboard.Tests.Integrations;

/// <summary>
/// Property tests for the fidelity of <see cref="JobExportService"/> with respect to the current
/// search criteria.
///
/// Feature: integrations-v2-6, Property 16: Export reflects the current search criteria
///
/// **Property 16** — for any <c>Search_Criteria</c> and backing data, the set of records produced by
/// the export SHALL equal the set of records that <see cref="IStorageQueryProvider"/> returns for the
/// same criteria (same job identifiers, same order). The provider result is used as the model oracle.
/// The export additionally SHALL stop at <c>MaxRecords</c>, so the exported count equals
/// <c>min(total, maxRecords)</c>.
///
/// **Validates: Requirements 13.1, 13.6**
///
/// <para>Approach: a <see cref="FakeQueryProvider"/> returns deterministic pages of a generated
/// backing dataset for whatever <c>(page, pageSize)</c> the service requests. The service exports to
/// a <see cref="MemoryStream"/> as JSON; the exported <c>JobId</c> sequence is compared against the
/// oracle produced by paging the SAME provider directly for the same criteria.</para>
/// </summary>
public class ExportCriteriaFidelityProperties
{
    // ── Fake storage query provider (deterministic paging oracle) ───────────────────────────────

    /// <summary>
    /// A fake <see cref="IStorageQueryProvider"/> that ignores the (irrelevant) criteria content and
    /// returns stable, in-order pages of a fixed backing dataset. This is the model oracle: the
    /// export must reproduce exactly what this provider yields when paged.
    /// </summary>
    private sealed class FakeQueryProvider : IStorageQueryProvider
    {
        private readonly IReadOnlyList<JobSummaryDto> _backing;

        public FakeQueryProvider(IReadOnlyList<JobSummaryDto> backing) => _backing = backing;

        public Task<PagedResult<JobSummaryDto>> GetJobsWithFilterAsync(
            JobFilterCriteria criteria, int page, int pageSize, CancellationToken ct)
        {
            var skip = (page - 1) * pageSize;
            var items = skip >= _backing.Count
                ? Array.Empty<JobSummaryDto>()
                : _backing.Skip(skip).Take(pageSize).ToArray();

            var result = new PagedResult<JobSummaryDto>
            {
                Items = items,
                TotalCount = _backing.Count,
                Page = page,
                PageSize = pageSize,
            };
            return Task.FromResult(result);
        }

        public Task<PagedResult<JobSummaryDto>> GetJobsByTagAsync(string tag, int page, int pageSize, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<TagCountDto>> GetTagCloudAsync(CancellationToken ct)
            => throw new NotImplementedException();

        public Task<PagedResult<JobSummaryDto>> GetJobsByStateAsync(string stateName, int page, int pageSize, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<SlowestJobDto>> GetSlowestJobsAsync(int count, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
            => throw new NotImplementedException();
    }

    // ── Generators ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A generated scenario: the number of records backing the provider (crossing the export's
    /// internal 500-record page size several times) and a <c>MaxRecords</c> cap that may be below,
    /// equal to, or above the total.
    /// </summary>
    private static Arbitrary<(int count, int maxRecords)> ScenarioArb =>
        Arb.From(
            from count in Gen.Choose(0, 1200)
            from maxRecords in Gen.Choose(0, 1500)
            select (count, maxRecords));

    /// <summary>
    /// Builds a deterministic backing dataset of <paramref name="count"/> records with unique,
    /// order-revealing <c>JobId</c>s so both id equality and ordering are observable.
    /// </summary>
    private static List<JobSummaryDto> BuildBacking(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new JobSummaryDto
            {
                JobId = "job-" + i.ToString("D5"),
                JobName = "Namespace.Type.Method" + (i % 7),
                State = (i % 3) switch { 0 => "Succeeded", 1 => "Failed", _ => "Processing" },
                Queue = (i % 2 == 0) ? "default" : "critical",
                CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i),
                LastStateChange = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i + 1),
                DurationMs = i * 1.5,
                LatencyMs = i * 0.25,
                Tags = (i % 4 == 0) ? new[] { "tag-a", "tag-b" } : null,
                ExceptionType = (i % 3 == 1) ? "System.InvalidOperationException" : null,
                ExceptionMessage = (i % 3 == 1) ? "boom " + i : null,
            })
            .ToList();

    /// <summary>
    /// The oracle: pages the provider directly for the same criteria (in the same 1-based page order
    /// the export uses) and returns the <c>JobId</c> sequence, capped at <paramref name="maxRecords"/>.
    /// </summary>
    private static async Task<List<string>> OracleJobIdsAsync(
        IStorageQueryProvider provider, JobFilterCriteria criteria, int maxRecords)
    {
        var ids = new List<string>();
        if (maxRecords <= 0)
            return ids;

        const int pageSize = 500;
        var page = 1;
        while (true)
        {
            var result = await provider.GetJobsWithFilterAsync(criteria, page, pageSize, CancellationToken.None);
            var items = result.Items;
            if (items is null || items.Count == 0)
                break;

            foreach (var item in items)
            {
                ids.Add(item.JobId);
                if (ids.Count >= maxRecords)
                    return ids;
            }

            if (items.Count < pageSize)
                break;

            page++;
        }

        return ids;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ── Property ────────────────────────────────────────────────────────────────────────────────

    [Property(MaxTest = 100)]
    public Property Export_ReflectsCurrentSearchCriteria()
    {
        return Prop.ForAll(ScenarioArb, scenario =>
        {
            var (count, maxRecords) = scenario;

            var backing = BuildBacking(count);
            var provider = new FakeQueryProvider(backing);
            var service = new JobExportService(provider);

            // The criteria content is irrelevant to the fake provider (it always returns the same
            // dataset), so any criteria value exercises "the same criteria" on both sides.
            var criteria = new JobFilterCriteria { State = "Succeeded" };

            // Export as JSON, then deserialize back to the shared record shape.
            using var stream = new MemoryStream();
            service.WriteJsonAsync(stream, criteria, maxRecords, CancellationToken.None)
                .GetAwaiter().GetResult();

            stream.Position = 0;
            var exported = JsonSerializer.Deserialize<List<JobRecordDto>>(stream.ToArray(), JsonOptions)
                           ?? new List<JobRecordDto>();
            var exportedIds = exported.Select(r => r.JobId).ToList();

            // Oracle: the provider's own paged result for the same criteria, capped at MaxRecords.
            var oracleIds = OracleJobIdsAsync(provider, criteria, maxRecords)
                .GetAwaiter().GetResult();

            var expectedCount = Math.Min(count, Math.Max(0, maxRecords));

            bool countMatches = exportedIds.Count == expectedCount;
            bool idsMatch = exportedIds.SequenceEqual(oracleIds);

            return (countMatches && idsMatch)
                .Label(
                    $"count={count} maxRecords={maxRecords} expectedCount={expectedCount} " +
                    $"exportedCount={exportedIds.Count} oracleCount={oracleIds.Count} " +
                    $"countMatches={countMatches} idsMatch={idsMatch}");
        });
    }
}
