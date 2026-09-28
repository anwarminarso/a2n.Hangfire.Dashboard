using System.Collections.Generic;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Services;
using Hangfire;
using Hangfire.Storage.Monitoring;
using Moq;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests;

public class FailedJobPageSourceTests
{
    [Fact]
    public void GetFailedJobs_UsesPageSource_InsteadOfMonitoringApi()
    {
        var expected = new JobList<FailedJobDto>(new[]
        {
            new KeyValuePair<string, FailedJobDto>("42", new FailedJobDto
            {
                ExceptionMessage = "boom",
            }),
        });

        var storage = new Mock<JobStorage>();
        storage.Setup(s => s.GetMonitoringApi())
            .Throws(new System.InvalidOperationException("monitoring api must not be called"));

        var service = new HangfireMonitorService(storage.Object, failedJobs: new StubSource(expected));

        var page = service.GetFailedJobs(0, 20);

        Assert.Same(expected, page);
    }

    private sealed class StubSource : IFailedJobPageSource
    {
        private readonly JobList<FailedJobDto> _page;
        public StubSource(JobList<FailedJobDto> page) => _page = page;
        public JobList<FailedJobDto> GetFailedJobs(int from, int count) => _page;
    }
}
