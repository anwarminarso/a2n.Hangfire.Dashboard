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
/// Thrown by <see cref="FailureGroupingTests"/> to wrap another exception, the way EF Core's
/// <c>DbUpdateException</c> wraps the database error. Top level so its name has no '+'.
/// </summary>
public class SaveChangesFailedException : Exception
{
    public SaveChangesFailedException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Grouping failed jobs by exception (#53): the generic provider on Hangfire.InMemory, and the
/// merging of stored and computed fingerprints that every provider shares.
/// </summary>
public class FailureGroupingTests
{
    public static class OrderJobs
    {
        public static void Run() { }
    }

    private static FailedState Fail(Exception exception)
    {
        try
        {
            throw exception;
        }
        catch (Exception ex)
        {
            return new FailedState(ex);
        }
    }

    private static readonly Job OrderJob = Job.FromExpression(() => OrderJobs.Run());

    private static (InMemoryStorage Storage, BackgroundJobClient Client, GenericQueryProvider Provider) InMemory()
    {
        var storage = new InMemoryStorage();
        var provider = new GenericQueryProvider(storage, new TagsDataReader(storage));
        // No filters: the global AutomaticRetry would turn the Failed state into a scheduled retry.
        return (storage, new BackgroundJobClient(storage, new JobFilterCollection()), provider);
    }

    private static string ExpectedFingerprint(JobStorage storage, string jobId)
    {
        using var connection = storage.GetConnection();
        return FailureFingerprint.Compute(
            connection.GetStateData(jobId).Data, InvocationData.SerializeJob(OrderJob).Type).Fingerprint;
    }

    // ─── Generic provider ────────────────────────────────────────────────────

    [Fact]
    public void GenericProvider_SupportsGrouping()
    {
        Assert.True(InMemory().Provider.SupportsFailureGrouping);
    }

    [Fact]
    public async Task GenericProvider_GroupsByException_MostJobsFirst()
    {
        var (storage, client, provider) = InMemory();
        var locked = Enumerable.Range(1, 3)
            .Select(i => client.Create(OrderJob, Fail(new InvalidOperationException($"Order {i} is locked"))))
            .ToList();
        var timeouts = Enumerable.Range(1, 2)
            .Select(_ => client.Create(OrderJob, Fail(new TimeoutException("The operation has timed out."))))
            .ToList();
        client.Create(OrderJob, new EnqueuedState());

        var result = await provider.GetFailureGroupsAsync(FailureGroupsResult.DefaultLabelCount, CancellationToken.None);

        Assert.True(result.IsSupported);
        // Job parameters aren't read here, so the page mustn't suggest the filter for exact counts.
        Assert.False(result.UsesStoredFingerprints);
        Assert.False(result.IsPartial);
        Assert.Equal(5, result.UnfingerprintedCount);
        Assert.Equal(5, result.FallbackCount);
        Assert.Equal(new long[] { 3, 2 }, result.Groups.Select(g => g.Count));

        var lockedGroup = result.Groups[0];
        Assert.Equal(ExpectedFingerprint(storage, locked[0]), lockedGroup.Fingerprint);
        Assert.Equal(0, lockedGroup.StoredCount);
        Assert.Equal(3, lockedGroup.ComputedCount);
        Assert.Equal(Enumerable.Reverse(locked), lockedGroup.ComputedJobIds);
        Assert.Equal(locked[^1], lockedGroup.NewestJobId);
        Assert.True(lockedGroup.IsLabeled);
        Assert.Equal("System.InvalidOperationException", lockedGroup.ExceptionType);
        Assert.Equal("", lockedGroup.InnerExceptionType);
        Assert.Equal("Order <n> is locked", lockedGroup.NormalizedMessage);
        Assert.NotNull(lockedGroup.LastFailedAt);

        Assert.Equal(ExpectedFingerprint(storage, timeouts[0]), result.Groups[1].Fingerprint);
        Assert.Equal("System.TimeoutException", result.Groups[1].ExceptionType);
    }

    [Fact]
    public async Task GenericProvider_WrappedExceptions_GroupByTheirInnerException()
    {
        var (_, client, provider) = InMemory();
        client.Create(OrderJob, Fail(new SaveChangesFailedException(
            "An error occurred while saving the entity changes.",
            new InvalidOperationException("Column 'Email' cannot be null"))));
        client.Create(OrderJob, Fail(new SaveChangesFailedException(
            "An error occurred while saving the entity changes.",
            new InvalidOperationException("Column 'Phone' cannot be null"))));

        var result = await provider.GetFailureGroupsAsync(FailureGroupsResult.DefaultLabelCount, CancellationToken.None);

        Assert.Equal(2, result.Groups.Count);
        Assert.All(result.Groups, g =>
        {
            Assert.Equal(typeof(SaveChangesFailedException).FullName, g.ExceptionType);
            Assert.Equal("System.InvalidOperationException", g.InnerExceptionType);
        });
        Assert.Equal(
            new[] { "Column 'Email' cannot be null", "Column 'Phone' cannot be null" },
            result.Groups.Select(g => g.NormalizedMessage).OrderBy(m => m));
    }

    [Fact]
    public async Task GenericProvider_MoreFailedJobsThanTheCap_IsPartial()
    {
        var failed = Enumerable.Range(1, JobFilterCriteria.CountLimit + 5)
            .Select(i => new KeyValuePair<string, FailedJobDto>(i.ToString(), new FailedJobDto
            {
                Job = OrderJob,
                ExceptionType = "System.TimeoutException",
                ExceptionMessage = "The operation has timed out.",
                FailedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(-i),
                InFailedState = true,
            }))
            .ToList();
        var monitoring = new Mock<IMonitoringApi>();
        monitoring.Setup(m => m.FailedCount()).Returns(failed.Count);
        monitoring.Setup(m => m.FailedJobs(It.IsAny<int>(), It.IsAny<int>()))
            .Returns<int, int>((from, count) => new JobList<FailedJobDto>(failed.Skip(from).Take(count)));
        var provider = ProviderOver(monitoring);

        var result = await provider.GetFailureGroupsAsync(FailureGroupsResult.DefaultLabelCount, CancellationToken.None);

        Assert.True(result.IsPartial);
        Assert.Equal(JobFilterCriteria.CountLimit + 5, result.UnfingerprintedCount);
        Assert.Equal(JobFilterCriteria.CountLimit, result.FallbackCount);
        var group = Assert.Single(result.Groups);
        Assert.Equal(JobFilterCriteria.CountLimit, group.Count);
        // The newest jobs are the ones read.
        Assert.Equal("1", group.NewestJobId);
        Assert.DoesNotContain(group.ComputedJobIds, id => int.Parse(id) > JobFilterCriteria.CountLimit);
    }

    [Fact]
    public async Task GenericProvider_OnInMemory_ReadsTheNewestFailedJobs_WhenThereAreMoreThanTheCap()
    {
        var (_, client, provider) = InMemory();
        // Older failures first, then CountLimit newer ones of another exception.
        for (var i = 0; i < 50; i++)
            client.Create(OrderJob, Fail(new TimeoutException("The old gateway did not answer.")));
        var newer = Enumerable.Range(1, JobFilterCriteria.CountLimit)
            .Select(_ => client.Create(OrderJob, Fail(new InvalidOperationException("The order is locked."))))
            .ToList();

        var result = await provider.GetFailureGroupsAsync(FailureGroupsResult.DefaultLabelCount, CancellationToken.None);

        Assert.True(result.IsPartial);
        var group = Assert.Single(result.Groups);
        Assert.Equal("System.InvalidOperationException", group.ExceptionType);
        Assert.Equal(newer[^1], group.NewestJobId);
    }

    [Fact]
    public async Task GenericProvider_JobWithAnUnloadableType_IsGrouped_UsingItsStoredType()
    {
        // The dashboard can't load Contoso.Billing; the job still failed, and its stored type still
        // tells the fingerprint which frame is the application's own.
        const string details =
            "Npgsql.PostgresException: 23505: duplicate key value violates unique constraint \"pk_invoice\"\n"
            + "   at Npgsql.Internal.NpgsqlConnector.ReadMessageLong(Boolean async)\n"
            + "   at Contoso.Billing.InvoiceJob.Run()";
        var monitoring = new Mock<IMonitoringApi>();
        monitoring.Setup(m => m.FailedCount()).Returns(1);
        monitoring.Setup(m => m.FailedJobs(It.IsAny<int>(), It.IsAny<int>()))
            .Returns<int, int>((from, _) => new JobList<FailedJobDto>(from > 0
                ? Array.Empty<KeyValuePair<string, FailedJobDto>>()
                : new[]
                {
                    new KeyValuePair<string, FailedJobDto>("7", new FailedJobDto
                    {
                        Job = null,
                        InvocationData = new InvocationData("Contoso.Billing.InvoiceJob, Contoso.Billing", "Run", "[]", "[]"),
                        ExceptionType = "Npgsql.PostgresException",
                        ExceptionMessage = "23505: duplicate key value violates unique constraint \"pk_invoice\"",
                        ExceptionDetails = details,
                        InFailedState = true,
                    }),
                }));
        var provider = ProviderOver(monitoring);

        var result = await provider.GetFailureGroupsAsync(FailureGroupsResult.DefaultLabelCount, CancellationToken.None);

        var group = Assert.Single(result.Groups);
        Assert.Equal(new[] { "7" }, group.ComputedJobIds);
        Assert.Equal("Contoso.Billing.InvoiceJob.Run", group.TopFrame);
    }

    [Fact]
    public async Task GenericProvider_ReadsFailedJobsFromThePageSource_WhenOneIsRegistered()
    {
        var monitoring = new Mock<IMonitoringApi>();
        monitoring.Setup(m => m.FailedCount()).Returns(1);
        monitoring.Setup(m => m.FailedJobs(It.IsAny<int>(), It.IsAny<int>()))
            .Throws(new KeyNotFoundException("FailedAt"));
        var source = new Mock<IFailedJobPageSource>();
        source.Setup(s => s.GetFailedJobs(It.IsAny<int>(), It.IsAny<int>()))
            .Returns<int, int>((from, _) => new JobList<FailedJobDto>(from > 0
                ? Array.Empty<KeyValuePair<string, FailedJobDto>>()
                : new[] { new KeyValuePair<string, FailedJobDto>("1", new FailedJobDto { ExceptionType = "System.Exception" }) }));
        var provider = ProviderOver(monitoring, source.Object);

        var result = await provider.GetFailureGroupsAsync(FailureGroupsResult.DefaultLabelCount, CancellationToken.None);

        Assert.Equal("System.Exception", Assert.Single(result.Groups).ExceptionType);
    }

    [Fact]
    public async Task GenericProvider_GroupJobs_AreTheNewestStillFailed_UpToTheCount()
    {
        var (_, client, provider) = InMemory();
        var ids = Enumerable.Range(1, 12)
            .Select(i => client.Create(OrderJob, Fail(new InvalidOperationException($"Order {i} is locked"))))
            .ToList();
        var groups = await provider.GetFailureGroupsAsync(FailureGroupsResult.DefaultLabelCount, CancellationToken.None);
        var group = Assert.Single(groups.Groups);

        // Requeued after the groups were read: no longer failed, so no longer listed.
        client.Requeue(ids[^1]);
        var jobs = await provider.GetFailureGroupJobsAsync(group, 10, CancellationToken.None);

        Assert.Equal(ids.AsEnumerable().Reverse().Skip(1).Take(10), jobs.Select(j => j.JobId));
        var newest = jobs[0];
        Assert.Equal("Failed", newest.State);
        Assert.Equal("System.InvalidOperationException", newest.ExceptionType);
        Assert.Equal("Order 11 is locked", newest.ExceptionMessage);
        Assert.Equal(typeof(OrderJobs).FullName, newest.InvocationData.Type.Split(',')[0]);
        Assert.NotNull(newest.LastStateChange);
    }

    [Fact]
    public async Task GenericProvider_GroupJobIds_AreEveryMemberStillFailed_NewestFirst()
    {
        var (storage, client, provider) = InMemory();
        var ids = Enumerable.Range(1, 12)
            .Select(i => client.Create(OrderJob, Fail(new InvalidOperationException($"Order {i} is locked"))))
            .ToList();
        var other = client.Create(OrderJob, Fail(new TimeoutException("The operation has timed out.")));
        var groups = await provider.GetFailureGroupsAsync(FailureGroupsResult.DefaultLabelCount, CancellationToken.None);
        var group = Assert.Single(groups.Groups, g => g.Fingerprint == ExpectedFingerprint(storage, ids[0]));

        // Left the Failed state after the groups were read.
        client.Requeue(ids[^1]);
        client.ChangeState(ids[0], new SucceededState(null, 0, 0));
        var jobIds = await provider.GetFailureGroupJobIdsAsync(group, CancellationToken.None);

        // More than the page shows: every member, unlike GetFailureGroupJobsAsync.
        Assert.Equal(ids.Skip(1).Take(10).Reverse(), jobIds);
        Assert.DoesNotContain(other, jobIds);
    }

    [Fact]
    public async Task GenericProvider_AJobCreatedFirstThatFailedLast_IsTheGroupsNewest()
    {
        // The same order as the SQL Server and PostgreSQL adapters (#71): by when a job failed.
        var (_, client, provider) = InMemory();
        var failedLast = client.Create(OrderJob, new ScheduledState(TimeSpan.FromHours(1)));
        var first = client.Create(OrderJob, Fail(new InvalidOperationException("Order 1 is locked")));
        var second = client.Create(OrderJob, Fail(new InvalidOperationException("Order 2 is locked")));
        client.ChangeState(failedLast, Fail(new InvalidOperationException("Order 3 is locked")));

        var group = Assert.Single((await provider.GetFailureGroupsAsync(FailureGroupsResult.DefaultLabelCount, CancellationToken.None)).Groups);

        var failedLastFirst = new[] { failedLast, second, first };
        Assert.Equal(failedLast, group.NewestJobId);
        Assert.Equal(failedLastFirst, group.ComputedJobIds);
        Assert.Equal(failedLastFirst, (await provider.GetFailureGroupJobsAsync(group, 10, CancellationToken.None)).Select(j => j.JobId));
        Assert.Equal(failedLastFirst, await provider.GetFailureGroupJobIdsAsync(group, CancellationToken.None));
    }

    [Fact]
    public async Task ProviderWithoutGrouping_ReportsItUnsupported()
    {
        IStorageQueryProvider provider = new Mock<IStorageQueryProvider> { CallBase = true }.Object;
        var group = new FailureGroupDto { Fingerprint = "v1:0123456789abcdef" };

        Assert.False(provider.SupportsFailureGrouping);
        Assert.False((await provider.GetFailureGroupsAsync(10, CancellationToken.None)).IsSupported);
        Assert.Empty(await provider.GetFailureGroupJobsAsync(group, 10, CancellationToken.None));
        Assert.Empty(await provider.GetFailureGroupJobIdsAsync(group, CancellationToken.None));
    }

    [Fact]
    public async Task GroupJobIds_DefaultsToEveryJobFromGetFailureGroupJobsAsync()
    {
        // A custom provider written before GetFailureGroupJobIdsAsync existed still gets group actions.
        var mock = new Mock<IStorageQueryProvider> { CallBase = true };
        var group = new FailureGroupDto { Fingerprint = "v1:0123456789abcdef", Count = 3 };
        mock.Setup(p => p.GetFailureGroupJobsAsync(group, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "9", "7", "4" }.Select(id => new JobSummaryDto { JobId = id }).ToList());

        var ids = await mock.Object.GetFailureGroupJobIdsAsync(group, CancellationToken.None);

        Assert.Equal(new[] { "9", "7", "4" }, ids);
        mock.Verify(p => p.GetFailureGroupJobsAsync(group, int.MaxValue, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static GenericQueryProvider ProviderOver(Mock<IMonitoringApi> monitoring, IFailedJobPageSource failedJobs = null)
    {
        var storage = new Mock<JobStorage>();
        storage.Setup(s => s.GetMonitoringApi()).Returns(monitoring.Object);
        return new GenericQueryProvider(storage.Object, new TagsDataReader(storage.Object), failedJobs);
    }

    // ─── Merging stored and computed fingerprints ───────────────────────────

    private static ComputedFailure Computed(long id, string message, DateTime? failedAt = null)
        => new(id.ToString(), id, FailureFingerprint.Compute("System.InvalidOperationException", message, ""), failedAt);

    [Fact]
    public async Task Build_MergesStoredAndComputed_IntoOneGroupPerFingerprint()
    {
        var a = Computed(5, "Order is locked");
        var stored = new[] { new StoredFailureGroup(a.Result.Fingerprint, 3, NewestJobId: 9) };

        var result = await FailureGrouping.BuildAsync(
            stored, unfingerprintedCount: 2, new[] { a, Computed(4, "Order is locked") }, labelCount: 0, null, CancellationToken.None);

        var group = Assert.Single(result.Groups);
        Assert.Equal(5, group.Count);
        Assert.Equal(3, group.StoredCount);
        Assert.Equal(2, group.ComputedCount);
        Assert.Equal(new[] { "5", "4" }, group.ComputedJobIds);
        Assert.Equal("9", group.NewestJobId);
        Assert.False(result.IsPartial);
    }

    [Fact]
    public async Task Build_LabelsOnlyTheLargestGroups_LoadingStoredSamplesInOneCall()
    {
        var timeout = FailureFingerprint.Compute("System.TimeoutException", "Timed out", "");
        var locked = Computed(20, "Order is locked", new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        var stored = new[]
        {
            new StoredFailureGroup(timeout.Fingerprint, 5, NewestJobId: 30),
            new StoredFailureGroup("v1:ffffffffffffffff", 1, NewestJobId: 31),
        };
        var loads = new List<IReadOnlyList<long>>();

        var result = await FailureGrouping.BuildAsync(
            stored, 2, new[] { locked, Computed(10, "Order is locked") }, labelCount: 2,
            (ids, _) =>
            {
                loads.Add(ids);
                return Task.FromResult<IReadOnlyList<ComputedFailure>>(
                    new[] { new ComputedFailure("30", 30, timeout, null) });
            },
            CancellationToken.None);

        Assert.Equal(new long[] { 5, 2, 1 }, result.Groups.Select(g => g.Count));
        Assert.Equal(new long[] { 30 }, Assert.Single(loads));
        Assert.Equal("System.TimeoutException", result.Groups[0].ExceptionType);
        Assert.Equal("Order is locked", result.Groups[1].NormalizedMessage);
        Assert.Equal(locked.FailedAt, result.Groups[1].LastFailedAt);
        Assert.True(result.Groups[1].IsLabeled);
        Assert.False(result.Groups[2].IsLabeled);
        Assert.Null(result.Groups[2].ExceptionType);
    }

    [Fact]
    public async Task Build_AStoredJobThatFailedLast_IsTheNewest_AndIsLoadedByItsId()
    {
        // Job 9 was created before job 40 but failed after it: its sequence (the adapters' state id)
        // is higher, though its id is lower.
        var failedAt = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);
        var computed = Computed(40, "Order is locked");
        var stored = new[] { new StoredFailureGroup(computed.Result.Fingerprint, 2, NewestJobId: 9) { NewestSequence = 100 } };
        var loads = new List<IReadOnlyList<long>>();

        var result = await FailureGrouping.BuildAsync(
            stored, 1, new[] { computed }, labelCount: 1,
            (ids, _) =>
            {
                loads.Add(ids);
                return Task.FromResult<IReadOnlyList<ComputedFailure>>(
                    new[] { new ComputedFailure("9", 100, computed.Result, failedAt) });
            },
            CancellationToken.None);

        var group = Assert.Single(result.Groups);
        Assert.Equal("9", group.NewestJobId);
        Assert.Equal(new long[] { 9 }, Assert.Single(loads));
        Assert.Equal(failedAt, group.LastFailedAt);
    }

    [Fact]
    public async Task Build_AComputedJobThatFailedLast_IsTheNewest_WithoutLoadingAnything()
    {
        var failedAt = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc);
        var computed = new ComputedFailure("4", 150, FailureFingerprint.Compute("System.InvalidOperationException", "Order is locked", ""), failedAt);
        var stored = new[] { new StoredFailureGroup(computed.Result.Fingerprint, 2, NewestJobId: 9) { NewestSequence = 100 } };
        var loads = 0;

        var result = await FailureGrouping.BuildAsync(
            stored, 1, new[] { computed }, labelCount: 1,
            (_, _) =>
            {
                loads++;
                return Task.FromResult<IReadOnlyList<ComputedFailure>>(Array.Empty<ComputedFailure>());
            },
            CancellationToken.None);

        var group = Assert.Single(result.Groups);
        Assert.Equal("4", group.NewestJobId);
        Assert.Equal(0, loads);
        Assert.Equal(failedAt, group.LastFailedAt);
    }

    [Fact]
    public void StoredFailureGroup_NewestSequence_DefaultsToTheJobId()
    {
        // So an adapter built against the three-argument constructor keeps ordering by job id.
        Assert.Equal(9, new StoredFailureGroup("v1:0123456789abcdef", 1, NewestJobId: 9).NewestSequence);
    }

    [Fact]
    public async Task Build_FewerJobsReadThanUnfingerprinted_IsPartial()
    {
        var result = await FailureGrouping.BuildAsync(
            null, unfingerprintedCount: 3, new[] { Computed(1, "x") }, 10, null, CancellationToken.None);

        Assert.True(result.IsPartial);
        Assert.Equal(1, result.FallbackCount);
    }

    // ─── Reading stored strings ──────────────────────────────────────────────

    [Fact]
    public void ReadFailedAt_PrefersFailedAt_ElseTheStateTime()
    {
        var stateTime = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Unspecified);
        var failedAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(failedAt, FailureGrouping.ReadFailedAt(
            new Dictionary<string, string> { ["FailedAt"] = JobHelper.SerializeDateTime(failedAt) }, stateTime));

        var fallback = FailureGrouping.ReadFailedAt(new Dictionary<string, string>(), stateTime);
        Assert.Equal(stateTime, fallback);
        Assert.Equal(DateTimeKind.Utc, fallback.Value.Kind);
        Assert.Equal(stateTime, FailureGrouping.ReadFailedAt(new Dictionary<string, string> { ["FailedAt"] = "not a date" }, stateTime));
        Assert.Null(FailureGrouping.ReadFailedAt(null, null));
    }

    [Fact]
    public void ReadStateData_And_ReadJobTypeName_ToleratesMissingOrBrokenInput()
    {
        Assert.Null(FailureGrouping.ReadStateData(null));
        Assert.Null(FailureGrouping.ReadStateData("{not json"));
        Assert.Equal("boom", FailureGrouping.ReadStateData("{\"exceptionmessage\":\"boom\"}")["ExceptionMessage"]);

        Assert.Null(FailureGrouping.ReadJobTypeName(null));
        Assert.Null(FailureGrouping.ReadJobTypeName("{not json"));
        Assert.Equal("MyApp.Jobs.OrderJob, MyApp",
            FailureGrouping.ReadJobTypeName("{\"t\":\"MyApp.Jobs.OrderJob, MyApp\",\"m\":\"Run\"}"));
    }

    [Fact]
    public void Compute_RowWithoutStateFields_StillHasAFingerprint()
    {
        var row = new FailedJobRow { Id = 3, StateCreatedAt = new DateTime(2026, 9, 1) };

        var computed = FailureGrouping.Compute(row);

        Assert.Equal("3", computed.JobId);
        Assert.True(FailureFingerprint.IsCurrentVersion(computed.Result.Fingerprint));
        Assert.Equal(FailureFingerprint.Compute(null, null, null).Fingerprint, computed.Result.Fingerprint);
        Assert.Equal(new DateTime(2026, 9, 1), computed.FailedAt);
    }

    [Fact]
    public void Compute_OrdersARowByItsStateId_OrByItsIdWithoutOne()
    {
        var row = new FailedJobRow { Id = 7, StateId = 120 };
        Assert.Equal(120, FailureGrouping.Compute(row).Sequence);

        row.StateId = null;
        Assert.Equal(7, FailureGrouping.Compute(row).Sequence);
    }
}
