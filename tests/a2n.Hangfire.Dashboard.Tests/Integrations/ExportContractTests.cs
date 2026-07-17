using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Middleware;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services.Export;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests.Integrations;

/// <summary>
/// Example-based unit tests for the CSV / JSON export contracts (task 9.8).
///
/// Feature: integrations-v2-6
///
/// These <c>[Fact]</c> tests complement the property tests (Properties 16 &amp; 17) by pinning down the
/// concrete HTTP response contracts and the streaming/memory guarantees:
/// <list type="bullet">
/// <item><description>The response advertises <c>Content-Disposition: attachment</c> and the correct
/// per-format content type — <c>text/csv; charset=utf-8</c> and
/// <c>application/json; charset=utf-8</c> (Req 13.5).</description></item>
/// <item><description>Export remains available when the dashboard is in read-only mode
/// (<c>IsReadOnly = true</c>) because export is a read operation (Req 14.3).</description></item>
/// <item><description>Buffer memory is bounded and does not scale with the number of exported records:
/// the maximum length passed to any single <see cref="Stream"/> write stays below a fixed cap whether
/// 100 or 5000 records are exported (Req 13.4).</description></item>
/// </list>
/// </summary>
public class ExportContractTests
{
    // ── Test doubles (private, to avoid collisions with the fakes in sibling test files) ──────────

    /// <summary>An authorization filter with a deterministic decision.</summary>
    private sealed class FixedAuthorizationFilter : IDashboardAuthorizationFilter
    {
        private readonly bool _authorized;
        public FixedAuthorizationFilter(bool authorized) => _authorized = authorized;
        public bool Authorize(HttpContext context) => _authorized;
    }

    /// <summary>
    /// A fake <see cref="IStorageQueryProvider"/> that returns stable, in-order pages of a fixed
    /// backing dataset for whatever <c>(page, pageSize)</c> the export requests.
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

    /// <summary>Minimal <see cref="IServiceProvider"/> exposing only the query provider.</summary>
    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly IStorageQueryProvider _provider;
        public SingleServiceProvider(IStorageQueryProvider provider) => _provider = provider;
        public object GetService(Type serviceType)
            => serviceType == typeof(IStorageQueryProvider) ? _provider : null;
    }

    /// <summary>
    /// A write-only <see cref="Stream"/> that records the maximum length passed to any single write
    /// (sync or async, array- or span-based). Used to observe the peak in-memory buffer flushed to
    /// the "network" so we can assert it stays bounded regardless of total record count (Req 13.4).
    /// </summary>
    private sealed class MaxWriteObservingStream : Stream
    {
        public long MaxWriteLength { get; private set; }
        public long TotalBytes { get; private set; }

        private void Record(int count)
        {
            TotalBytes += count;
            if (count > MaxWriteLength)
                MaxWriteLength = count;
        }

        public override void Write(byte[] buffer, int offset, int count) => Record(count);

        public override void Write(ReadOnlySpan<byte> buffer) => Record(buffer.Length);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Record(count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Record(buffer.Length);
            return ValueTask.CompletedTask;
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => TotalBytes;
        public override long Position { get => TotalBytes; set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private static List<JobSummaryDto> BuildBacking(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new JobSummaryDto
            {
                JobId = "job-" + i.ToString("D5"),
                JobName = "Ns.Type.Method",
                State = "Succeeded",
                Queue = "default",
                CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i),
                LastStateChange = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i + 1),
                DurationMs = i * 1.5,
                LatencyMs = i * 0.25,
            })
            .ToList();

    private static DefaultHttpContext BuildContext(string format, Stream body, IStorageQueryProvider provider)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new SingleServiceProvider(provider),
        };
        context.Request.Method = "GET";
        context.Request.Path = new PathString("/export");
        context.Request.QueryString = new QueryString($"?format={format}");
        context.Response.Body = body;
        return context;
    }

    private static DashboardUIOptions BuildOptions(bool isReadOnly = false)
    {
        var options = new DashboardUIOptions
        {
            Authorization = new IDashboardAuthorizationFilter[] { new FixedAuthorizationFilter(true) },
            AsyncAuthorization = Array.Empty<IDashboardAsyncAuthorizationFilter>(),
            IsReadOnly = isReadOnly,
        };
        options.Export.Enabled = true;
        options.Export.Path = "/export";
        return options;
    }

    // ── Content-Disposition + per-format content type (Req 13.5) ────────────────────────────────────

    [Fact]
    public async Task Csv_export_sets_attachment_disposition_and_csv_content_type()
    {
        var provider = new FakeQueryProvider(BuildBacking(3));
        using var body = new MemoryStream();
        var context = BuildContext("csv", body, provider);
        var options = BuildOptions();

        var handled = await ExportEndpoint.TryHandleAsync(context, options);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("text/csv; charset=utf-8", context.Response.ContentType);

        var disposition = context.Response.Headers["Content-Disposition"].ToString();
        Assert.StartsWith("attachment", disposition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".csv", disposition, StringComparison.OrdinalIgnoreCase);
        Assert.True(body.Length > 0, "expected CSV records to be streamed");
    }

    [Fact]
    public async Task Json_export_sets_attachment_disposition_and_json_content_type()
    {
        var provider = new FakeQueryProvider(BuildBacking(3));
        using var body = new MemoryStream();
        var context = BuildContext("json", body, provider);
        var options = BuildOptions();

        var handled = await ExportEndpoint.TryHandleAsync(context, options);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);

        var disposition = context.Response.Headers["Content-Disposition"].ToString();
        Assert.StartsWith("attachment", disposition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".json", disposition, StringComparison.OrdinalIgnoreCase);
        Assert.True(body.Length > 0, "expected JSON records to be streamed");
    }

    // ── Export works in read-only mode (Req 14.3) ───────────────────────────────────────────────────

    [Fact]
    public async Task Export_succeeds_when_dashboard_is_read_only()
    {
        var provider = new FakeQueryProvider(BuildBacking(5));
        using var body = new MemoryStream();
        var context = BuildContext("csv", body, provider);
        var options = BuildOptions(isReadOnly: true);

        var handled = await ExportEndpoint.TryHandleAsync(context, options);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(body.Length > 0, "export must remain available in read-only mode (Req 14.3)");

        // The streamed CSV contains the header row plus one row per record.
        body.Position = 0;
        var text = new StreamReader(body).ReadToEnd();
        Assert.Contains("JobId,JobName,State", text);
        Assert.Contains("job-00000", text);
    }

    // ── Buffer memory is bounded and independent of record count (Req 13.4) ──────────────────────────

    // A fixed cap smaller than the total serialized size of the 5000-record dataset. If the writer
    // buffered the whole result set instead of streaming page-by-page, the peak single write for 5000
    // records would exceed this cap. Because the service flushes per page, the peak stays well below it
    // and does not grow with the total record count.
    private const long MaxSingleWriteCap = 256 * 1024;

    [Fact]
    public async Task Csv_export_buffer_is_bounded_and_independent_of_record_count()
    {
        var criteria = new JobFilterCriteria();

        var peakSmall = await MeasureCsvPeakAsync(recordCount: 100, criteria);
        var peakLarge = await MeasureCsvPeakAsync(recordCount: 5000, criteria);

        Assert.True(peakSmall.max < MaxSingleWriteCap,
            $"100-record CSV peak write {peakSmall.max} exceeded cap {MaxSingleWriteCap}");
        Assert.True(peakLarge.max < MaxSingleWriteCap,
            $"5000-record CSV peak write {peakLarge.max} exceeded cap {MaxSingleWriteCap}");

        // Sanity: the 5000-record export really did stream far more total bytes than the cap, so the
        // bounded peak is a meaningful (non-vacuous) result.
        Assert.True(peakLarge.total > MaxSingleWriteCap,
            "expected the 5000-record CSV export to exceed the cap in total bytes");
    }

    [Fact]
    public async Task Json_export_buffer_is_bounded_and_independent_of_record_count()
    {
        var criteria = new JobFilterCriteria();

        var peakSmall = await MeasureJsonPeakAsync(recordCount: 100, criteria);
        var peakLarge = await MeasureJsonPeakAsync(recordCount: 5000, criteria);

        Assert.True(peakSmall.max < MaxSingleWriteCap,
            $"100-record JSON peak write {peakSmall.max} exceeded cap {MaxSingleWriteCap}");
        Assert.True(peakLarge.max < MaxSingleWriteCap,
            $"5000-record JSON peak write {peakLarge.max} exceeded cap {MaxSingleWriteCap}");

        Assert.True(peakLarge.total > MaxSingleWriteCap,
            "expected the 5000-record JSON export to exceed the cap in total bytes");
    }

    private static async Task<(long max, long total)> MeasureCsvPeakAsync(int recordCount, JobFilterCriteria criteria)
    {
        var provider = new FakeQueryProvider(BuildBacking(recordCount));
        var service = new JobExportService(provider);
        var observer = new MaxWriteObservingStream();

        await service.WriteCsvAsync(observer, criteria, maxRecords: int.MaxValue, CancellationToken.None);

        return (observer.MaxWriteLength, observer.TotalBytes);
    }

    private static async Task<(long max, long total)> MeasureJsonPeakAsync(int recordCount, JobFilterCriteria criteria)
    {
        var provider = new FakeQueryProvider(BuildBacking(recordCount));
        var service = new JobExportService(provider);
        var observer = new MaxWriteObservingStream();

        await service.WriteJsonAsync(observer, criteria, maxRecords: int.MaxValue, CancellationToken.None);

        return (observer.MaxWriteLength, observer.TotalBytes);
    }
}
