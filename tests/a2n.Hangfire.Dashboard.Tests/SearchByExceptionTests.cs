using System;
using System.Collections.Generic;
using System.Linq;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Moq;
using Xunit;
using a2n.Hangfire.Dashboard.Helpers;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services;
using a2n.Hangfire.Dashboard.Storage;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// The Exception filter on Search (#53): jobs are matched by the failure fingerprint
/// <see cref="FailureFingerprintFilter"/> stored on them, in any state. Covers the criteria, the
/// request mapping and the generic provider (Redis and others) on Hangfire.InMemory.
/// </summary>
public class SearchByExceptionTests
{
    private const string Key = "v1:0123456789abcdef";

    public static class OrderJob
    {
        public static void Run() { }
    }

    public static class InvoiceJob
    {
        public static void Run() { }
    }

    private static readonly Job Order = Job.FromExpression(() => OrderJob.Run());
    private static readonly Job Invoice = Job.FromExpression(() => InvoiceJob.Run());

    // Every failure is thrown from here, so jobs failing with the same message share a fingerprint.
    private static FailedState Fail(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (Exception ex)
        {
            return new FailedState(ex);
        }
    }

    private sealed class InMemoryFixture
    {
        public InMemoryFixture()
        {
            Storage = new InMemoryStorage();
            var tags = new TagsDataReader(Storage);
            Provider = new GenericQueryProvider(Storage, tags);
            Search = new SearchService(Storage, tags, Provider);
            // The real filter, and no AutomaticRetry, which would turn Failed into a scheduled retry.
            WithFilter = new BackgroundJobClient(Storage, new JobFilterCollection { new FailureFingerprintFilter() });
            WithoutFilter = new BackgroundJobClient(Storage, new JobFilterCollection());
        }

        public JobStorage Storage { get; }
        public GenericQueryProvider Provider { get; }
        public SearchService Search { get; }
        public BackgroundJobClient WithFilter { get; }
        public BackgroundJobClient WithoutFilter { get; }

        // What a worker does with a requeued job: takes it off the queue and records the outcome.
        public void RunFromQueue(string jobId)
        {
            using (var connection = Storage.GetConnection())
            using (var fetched = connection.FetchNextJob(new[] { EnqueuedState.DefaultQueue }, CancellationToken.None))
            {
                Assert.Equal(jobId, fetched.JobId);
                fetched.RemoveFromQueue();
            }
            WithoutFilter.ChangeState(jobId, new SucceededState(null, 0, 0));
        }

        public string StoredFingerprint(string jobId)
        {
            using var connection = Storage.GetConnection();
            return connection.GetJobParameter(jobId, FailureFingerprint.ParameterName);
        }

        public async Task<List<string>> SearchIds(SearchRequest request)
        {
            var result = await Search.SearchAsync(request, CancellationToken.None);
            Assert.False(result.HasError, result.ErrorMessage);
            return result.Items.Select(i => i.JobId).OrderBy(id => id, StringComparer.Ordinal).ToList();
        }
    }

    private static List<string> Sorted(params string[] ids) => ids.OrderBy(id => id, StringComparer.Ordinal).ToList();

    // ─── Criteria and request mapping ───────────────────────────────────────

    [Theory]
    [InlineData(Key, true)]
    [InlineData("V1:0123456789ABCDEF", false)]
    [InlineData("v1:0123456789abcdef ", false)]
    [InlineData("v0:0123456789abcdef", false)]
    [InlineData("not-a-fingerprint", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void HasAnyCriteria_CountsOnlyACurrentFingerprint(string value, bool expected)
    {
        var criteria = new JobFilterCriteria { FailureFingerprint = value };

        Assert.Equal(expected, criteria.HasAnyCriteria());
        Assert.Equal(expected ? value : null, criteria.EffectiveFailureFingerprint);
    }

    [Fact]
    public async Task SearchService_MapsAValidFingerprint_AndDropsAMalformedOne()
    {
        var provider = new Mock<IStorageQueryProvider>();
        var seen = new List<JobFilterCriteria>();
        provider.Setup(p => p.GetJobsWithFilterAsync(It.IsAny<JobFilterCriteria>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback((JobFilterCriteria c, int _, int _, CancellationToken _) => seen.Add(c))
            .ReturnsAsync(PagedResult<JobSummaryDto>.Empty(1, 20));
        var storage = new InMemoryStorage();
        var search = new SearchService(storage, new TagsDataReader(storage), provider.Object);

        await search.SearchAsync(new SearchRequest { ExceptionFingerprint = Key }, CancellationToken.None);
        // A hand-edited URL: on its own it is no criterion at all, so nothing is queried.
        await search.SearchAsync(new SearchRequest { ExceptionFingerprint = "V1:0123456789ABCDEF" }, CancellationToken.None);
        // Next to another filter it is simply left out.
        await search.SearchAsync(new SearchRequest { ExceptionFingerprint = "v1:nope", States = new() { "Failed" } }, CancellationToken.None);

        Assert.Equal(2, seen.Count);
        Assert.Equal(Key, seen[0].FailureFingerprint);
        Assert.Null(seen[1].FailureFingerprint);
        Assert.Equal(new[] { "Failed" }, seen[1].States);
    }

    // ─── Generic provider on Hangfire.InMemory ──────────────────────────────

    [Fact]
    public async Task StoredFingerprints_MatchInFailedSucceededAndDeleted()
    {
        var f = new InMemoryFixture();
        var failed = f.WithFilter.Create(Order, Fail("Order 1 is locked"));
        var key = f.StoredFingerprint(failed);
        Assert.True(FailureFingerprint.IsCurrentVersion(key));

        // Requeued after failing, picked up by a worker and succeeded; the parameter stays on the job.
        var succeeded = f.WithFilter.Create(Order, Fail("Order 2 is locked"));
        f.WithoutFilter.Requeue(succeeded);
        f.RunFromQueue(succeeded);
        var deleted = f.WithFilter.Create(Order, Fail("Order 3 is locked"));
        f.WithoutFilter.Delete(deleted);
        var otherException = f.WithFilter.Create(Order, Fail("Order 4 could not be found"));
        // The same failure, but recorded without the filter: only the Failed page's fallback knows it.
        var unfingerprinted = f.WithoutFilter.Create(Order, Fail("Order 5 is locked"));

        var ids = await f.SearchIds(new SearchRequest { ExceptionFingerprint = key });

        Assert.Equal(Sorted(failed, succeeded, deleted), ids);
        Assert.DoesNotContain(otherException, ids);
        Assert.DoesNotContain(unfingerprinted, ids);
    }

    [Fact]
    public async Task TheFilter_CombinesWithStateAndTheOtherFilters()
    {
        var f = new InMemoryFixture();
        var failedOrder = f.WithFilter.Create(Order, Fail("Order 1 is locked"));
        var key = f.StoredFingerprint(failedOrder);
        var succeededOrder = f.WithFilter.Create(Order, Fail("Order 2 is locked"));
        f.WithoutFilter.ChangeState(succeededOrder, new SucceededState(null, 0, 0));
        // Another job type, so another fingerprint; given this one's key by hand to isolate the name filter.
        var failedInvoice = f.WithoutFilter.Create(Invoice, Fail("Order 3 is locked"));
        using (var connection = f.Storage.GetConnection())
            connection.SetJobParameter(failedInvoice, FailureFingerprint.ParameterName, key);

        Assert.Equal(Sorted(failedOrder, failedInvoice),
            await f.SearchIds(new SearchRequest { ExceptionFingerprint = key, States = new() { "Failed" } }));
        Assert.Equal(Sorted(succeededOrder),
            await f.SearchIds(new SearchRequest { ExceptionFingerprint = key, States = new() { "Succeeded" } }));
        Assert.Equal(Sorted(failedOrder, succeededOrder),
            await f.SearchIds(new SearchRequest { ExceptionFingerprint = key, Query = "OrderJob" }));
        Assert.Empty(await f.SearchIds(new SearchRequest { ExceptionFingerprint = key, ArgumentsQuery = "no such argument" }));
    }

    [Fact]
    public async Task ANonCurrentStoredValue_IsNotAMatch()
    {
        var f = new InMemoryFixture();
        var current = f.WithFilter.Create(Order, Fail("Order 1 is locked"));
        var key = f.StoredFingerprint(current);
        var upperCase = f.WithoutFilter.Create(Order, Fail("Order 2 is locked"));
        var padded = f.WithoutFilter.Create(Order, Fail("Order 3 is locked"));
        using (var connection = f.Storage.GetConnection())
        {
            connection.SetJobParameter(upperCase, FailureFingerprint.ParameterName, key.ToUpperInvariant());
            connection.SetJobParameter(padded, FailureFingerprint.ParameterName, key + " ");
        }

        Assert.Equal(Sorted(current), await f.SearchIds(new SearchRequest { ExceptionFingerprint = key }));
    }

    [Fact]
    public async Task TheParameter_IsReadRaw_AndOnlyWhenTheFilterIsSet()
    {
        var storage = new Mock<JobStorage>();
        var monitoring = new Mock<IMonitoringApi>();
        var connection = new Mock<JobStorageConnection>();
        storage.Setup(s => s.GetMonitoringApi()).Returns(monitoring.Object);
        storage.Setup(s => s.GetReadOnlyConnection()).Returns(connection.Object);
        monitoring.Setup(m => m.SucceededJobs(It.IsAny<int>(), It.IsAny<int>()))
            .Returns((int from, int _) => new JobList<SucceededJobDto>(from > 0
                ? Enumerable.Empty<KeyValuePair<string, SucceededJobDto>>()
                : new[] { "1", "2" }.Select(id => new KeyValuePair<string, SucceededJobDto>(id, new SucceededJobDto { Job = Order, SucceededAt = DateTime.UtcNow }))));
        // As FailureFingerprintFilter stores it: the plain string, which isn't valid JSON.
        connection.Setup(c => c.GetJobParameter("1", FailureFingerprint.ParameterName)).Returns(Key);
        connection.Setup(c => c.GetJobParameter("2", FailureFingerprint.ParameterName)).Returns((string)null);
        var provider = new GenericQueryProvider(storage.Object, new TagsDataReader(storage.Object));

        var filtered = await provider.GetJobsWithFilterAsync(
            new JobFilterCriteria { FailureFingerprint = Key, States = new() { "Succeeded" } }, 1, 20, CancellationToken.None);
        Assert.Equal(new[] { "1" }, filtered.Items.Select(j => j.JobId));
        connection.Verify(c => c.GetJobParameter(It.IsAny<string>(), FailureFingerprint.ParameterName), Times.Exactly(2));

        connection.Invocations.Clear();
        storage.Invocations.Clear();
        var unfiltered = await provider.GetJobsWithFilterAsync(
            new JobFilterCriteria { States = new() { "Succeeded" } }, 1, 20, CancellationToken.None);
        Assert.Equal(2, unfiltered.TotalCount);
        connection.Verify(c => c.GetJobParameter(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        storage.Verify(s => s.GetReadOnlyConnection(), Times.Never);
    }
}
