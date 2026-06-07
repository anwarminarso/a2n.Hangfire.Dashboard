using System;
using System.Collections.Generic;
using System.Linq;
using a2n.Hangfire.Dashboard.Helpers;
using Hangfire.Storage.Monitoring;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests;

public class RetrySummaryTests
{
    private static StateHistoryDto State(string name, string exceptionType = null)
    {
        var dto = new StateHistoryDto
        {
            StateName = name,
            CreatedAt = DateTime.UtcNow,
        };
        if (exceptionType is not null)
        {
            dto.Data = new Dictionary<string, string> { ["ExceptionType"] = exceptionType };
        }
        else if (name == "Failed")
        {
            dto.Data = new Dictionary<string, string>();
        }
        return dto;
    }

    // History is stored newest-first in Hangfire. Helper builds it in that order from a
    // chronological (oldest-first) sequence.
    private static JobDetailsDto Job(params StateHistoryDto[] chronological)
        => new() { History = chronological.Reverse().ToList() };

    [Fact]
    public void NullJob_DoesNotDisplay()
    {
        var summary = RetrySummary.FromHistory(null);
        Assert.False(summary.ShouldDisplay);
        Assert.Equal(0, summary.RetryCount);
        Assert.Empty(summary.AttemptIndex);
    }

    [Fact]
    public void EmptyHistory_DoesNotDisplay()
    {
        var summary = RetrySummary.FromHistory(new JobDetailsDto { History = new List<StateHistoryDto>() });
        Assert.False(summary.ShouldDisplay);
        Assert.Equal(0, summary.RetryCount);
    }

    [Fact]
    public void SuccessOnFirstAttempt_NoRetries()
    {
        var job = Job(
            State("Enqueued"),
            State("Processing"),
            State("Succeeded"));

        var summary = RetrySummary.FromHistory(job);

        Assert.False(summary.ShouldDisplay);
        Assert.Equal(0, summary.RetryCount);
        Assert.Equal("Succeeded", summary.FinalState);
    }

    [Fact]
    public void SucceededAfterTwoFailures_CountsBothAsRetries()
    {
        // Processing -> Failed -> Processing -> Failed -> Processing -> Succeeded
        var job = Job(
            State("Processing"),
            State("Failed", "System.TimeoutException"),
            State("Processing"),
            State("Failed", "System.TimeoutException"),
            State("Processing"),
            State("Succeeded"));

        var summary = RetrySummary.FromHistory(job);

        Assert.True(summary.ShouldDisplay);
        // Both failures triggered another attempt that ultimately succeeded.
        Assert.Equal(2, summary.RetryCount);
        Assert.Equal("Succeeded", summary.FinalState);
        Assert.True(summary.AllSameException);
        Assert.Single(summary.DistinctExceptionTypes);
    }

    [Fact]
    public void TerminalFailure_LastFailureIsNotARetry()
    {
        // Processing -> Failed -> Processing -> Failed (terminal)
        var job = Job(
            State("Processing"),
            State("Failed", "System.IO.IOException"),
            State("Processing"),
            State("Failed", "System.IO.IOException"));

        var summary = RetrySummary.FromHistory(job);

        // 2 failures, ends Failed => 1 retry (the terminal failure is not a retry).
        Assert.Equal(1, summary.RetryCount);
        Assert.Equal("Failed", summary.FinalState);
        Assert.True(summary.AllSameException);
    }

    [Fact]
    public void DifferentExceptions_AllSameExceptionFalse()
    {
        var job = Job(
            State("Processing"),
            State("Failed", "System.TimeoutException"),
            State("Processing"),
            State("Failed", "System.NullReferenceException"),
            State("Processing"),
            State("Succeeded"));

        var summary = RetrySummary.FromHistory(job);

        Assert.Equal(2, summary.RetryCount);
        Assert.False(summary.AllSameException);
        Assert.Equal(2, summary.DistinctExceptionTypes.Count);
        Assert.Equal("2 different exceptions", summary.GetExceptionPhrase());
    }

    [Fact]
    public void FailedWithoutExceptionData_RecordedAsUnknown()
    {
        var failedNoData = new StateHistoryDto { StateName = "Failed", CreatedAt = DateTime.UtcNow, Data = new Dictionary<string, string>() };
        var job = Job(
            State("Processing"),
            failedNoData,
            State("Processing"),
            State("Failed", "System.Exception"));

        var summary = RetrySummary.FromHistory(job);

        Assert.Equal(1, summary.RetryCount); // terminal failure not counted
        Assert.Contains("(unknown)", summary.DistinctExceptionTypes);
    }

    [Fact]
    public void AttemptIndex_NumbersProcessingAndMatchingFailure()
    {
        var p1 = State("Processing");
        var f1 = State("Failed", "System.TimeoutException");
        var p2 = State("Processing");
        var succeeded = State("Succeeded");
        var job = Job(p1, f1, p2, succeeded);

        var summary = RetrySummary.FromHistory(job);

        Assert.Equal(1, summary.AttemptIndex[p1]);
        Assert.Equal(1, summary.AttemptIndex[f1]);
        Assert.Equal(2, summary.AttemptIndex[p2]);
        // Succeeded states are not assigned an attempt number.
        Assert.False(summary.AttemptIndex.ContainsKey(succeeded));
    }

    [Fact]
    public void AttemptIndex_EmptyWhenNoRetries()
    {
        var job = Job(State("Processing"), State("Succeeded"));
        var summary = RetrySummary.FromHistory(job);
        Assert.Empty(summary.AttemptIndex);
    }

    [Fact]
    public void GetExceptionPhrase_SingleRetrySameException()
    {
        var job = Job(
            State("Processing"),
            State("Failed", "MyApp.Services.WidgetException"),
            State("Processing"),
            State("Succeeded"));

        var summary = RetrySummary.FromHistory(job);

        Assert.Equal(1, summary.RetryCount);
        // Short type name is used in the phrase.
        Assert.Contains("WidgetException", summary.GetExceptionPhrase());
    }
}
