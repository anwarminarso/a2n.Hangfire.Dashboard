using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Moq;
using Xunit;
using a2n.Hangfire.Dashboard.Helpers;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// The generic provider's search keeps jobs whose type the dashboard can't load (issue #70). Their
/// <c>Job</c> is null, as Hangfire.SqlServer returns it when <c>DeserializeJob</c> fails, but the
/// invocation data is there. Before, every scan skipped them, so a dashboard deployed without the
/// job assemblies found nothing at all.
/// </summary>
public class GenericSearchUnloadableJobTests
{
    private const string JobId = "7";

    // A type in an assembly the test process doesn't have.
    private static readonly InvocationData Invocation = new(
        "Contoso.Billing.InvoiceJobs, Contoso.Billing",
        "Send",
        "[\"System.String\"]",
        "[\"\\\"INV-42\\\"\"]",
        "billing");

    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<IMonitoringApi> _monitoring = new();
    private readonly Mock<JobStorageConnection> _connection = new();
    private readonly GenericQueryProvider _provider;

    public GenericSearchUnloadableJobTests()
    {
        _storage.Setup(s => s.GetMonitoringApi()).Returns(_monitoring.Object);
        _storage.Setup(s => s.GetReadOnlyConnection()).Returns(_connection.Object);
        _monitoring.Setup(m => m.Queues()).Returns(new List<QueueWithTopEnqueuedJobsDto>());
        _provider = new GenericQueryProvider(_storage.Object, new TagsDataReader(_storage.Object));
    }

    private static JobList<T> Page<T>(int from, params (string Id, T Dto)[] jobs)
        => new(from > 0
            ? Enumerable.Empty<KeyValuePair<string, T>>()
            : jobs.Select(j => new KeyValuePair<string, T>(j.Id, j.Dto)));

    private void WithFailed(params (string Id, FailedJobDto Dto)[] jobs)
    {
        _monitoring.Setup(m => m.FailedCount()).Returns(jobs.Length);
        _monitoring.Setup(m => m.FailedJobs(It.IsAny<int>(), It.IsAny<int>()))
            .Returns((int from, int _) => Page(from, jobs));
    }

    private static FailedJobDto UnloadableFailure() => new()
    {
        Job = null,
        InvocationData = Invocation,
        FailedAt = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
        ExceptionType = "System.TimeoutException",
        ExceptionMessage = "The billing gateway did not answer.",
        ExceptionDetails = "System.TimeoutException: The billing gateway did not answer.\n   at Contoso.Billing.InvoiceJobs.Send(String id)",
    };

    private Task<PagedResult<JobSummaryDto>> Search(JobFilterCriteria criteria)
        => _provider.GetJobsWithFilterAsync(criteria, 1, 20, CancellationToken.None);

    [Fact]
    public async Task AnUnloadableJob_IsListed_AndNamedFromItsInvocationData()
    {
        WithFailed((JobId, UnloadableFailure()));

        var result = await Search(new JobFilterCriteria { States = new() { "Failed" } });

        var job = Assert.Single(result.Items);
        Assert.Equal(JobId, job.JobId);
        Assert.Equal("InvoiceJobs.Send", job.JobName);
        Assert.Equal("billing", job.Queue);
        Assert.Equal("System.TimeoutException", job.ExceptionType);
    }

    [Theory]
    [InlineData("InvoiceJobs", true)]
    [InlineData("send", true)]
    [InlineData("Payroll", false)]
    public async Task TheNameFilter_MatchesTheInvocationData(string pattern, bool found)
    {
        WithFailed((JobId, UnloadableFailure()));

        var result = await Search(new JobFilterCriteria { JobNamePattern = pattern, States = new() { "Failed" } });

        Assert.Equal(found ? new[] { JobId } : Array.Empty<string>(), result.Items.Select(j => j.JobId));
    }

    [Fact]
    public async Task TheArgumentsFilter_MatchesTheInvocationData()
    {
        WithFailed((JobId, UnloadableFailure()));

        Assert.Single((await Search(new JobFilterCriteria { ArgumentsPattern = "INV-42", States = new() { "Failed" } })).Items);
        Assert.Empty((await Search(new JobFilterCriteria { ArgumentsPattern = "INV-43", States = new() { "Failed" } })).Items);
    }

    [Fact]
    public async Task TheOtherStates_KeepUnloadableJobs_Too()
    {
        _monitoring.Setup(m => m.DeletedJobs(It.IsAny<int>(), It.IsAny<int>()))
            .Returns((int from, int _) => Page(from, ("8", new DeletedJobDto { Job = null, InvocationData = Invocation, DeletedAt = DateTime.UtcNow })));
        _monitoring.Setup(m => m.SucceededJobs(It.IsAny<int>(), It.IsAny<int>()))
            .Returns((int from, int _) => Page(from, ("9", new SucceededJobDto { Job = null, InvocationData = Invocation, SucceededAt = DateTime.UtcNow })));

        var result = await Search(new JobFilterCriteria { JobNamePattern = "InvoiceJobs", States = new() { "Deleted", "Succeeded" } });

        Assert.Equal(new[] { "8", "9" }, result.Items.Select(j => j.JobId).OrderBy(id => id));
        Assert.All(result.Items, j => Assert.Equal("billing", j.Queue));
    }

    [Fact]
    public async Task AJobWithNeitherJobNorInvocationData_IsStillSkipped()
    {
        WithFailed((JobId, new FailedJobDto { Job = null, InvocationData = null, FailedAt = DateTime.UtcNow }));

        var result = await Search(new JobFilterCriteria { States = new() { "Failed" } });

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task TheFailedPagesGroup_AndSearch_FindTheSameUnloadableJob()
    {
        WithFailed((JobId, UnloadableFailure()));
        _connection.Setup(c => c.GetStateData(JobId)).Returns(new StateData { Name = "Failed" });

        var groups = await _provider.GetFailureGroupsAsync(50, CancellationToken.None);
        var group = Assert.Single(groups.Groups);
        var groupIds = await _provider.GetFailureGroupJobIdsAsync(group, CancellationToken.None);

        // As FailureFingerprintFilter stores it on the job: the same value the grouping computed.
        _connection.Setup(c => c.GetJobParameter(JobId, FailureFingerprint.ParameterName)).Returns(group.Fingerprint);
        var found = await Search(new JobFilterCriteria { FailureFingerprint = group.Fingerprint, States = new() { "Failed" } });

        Assert.Equal(new[] { JobId }, groupIds);
        Assert.Equal(groupIds, found.Items.Select(j => j.JobId));
    }

    [Theory]
    [InlineData("Contoso.Billing.InvoiceJobs, Contoso.Billing", "Send", "InvoiceJobs.Send")]
    [InlineData("Contoso.Billing.Outer+Inner, Contoso.Billing", "Run", "Inner.Run")]
    [InlineData("Contoso.Repo`1[[System.String, mscorlib]], Contoso", "Save", "Repo`1.Save")]
    [InlineData("InvoiceJobs", null, "InvoiceJobs")]
    public void TheName_IsTheClassName_AsTypeNameGivesIt(string type, string method, string expected)
    {
        Assert.Equal(expected, JobNameHelper.GetTypeAndMethod(null, new InvocationData(type, method, null, null)));
    }
}
