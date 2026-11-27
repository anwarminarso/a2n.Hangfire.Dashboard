namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Tracks active SignalR analytics/metrics subscriptions to avoid broadcasting when nobody is listening.
/// </summary>
public sealed class DashboardSubscriptionTracker
{
    private int _metricsSubscribers;
    private int _analyticsSubscribers;

    public bool HasMetricsSubscribers => Volatile.Read(ref _metricsSubscribers) > 0;

    public bool HasAnalyticsSubscribers => Volatile.Read(ref _analyticsSubscribers) > 0;

    public void AddMetricsSubscriber() => Interlocked.Increment(ref _metricsSubscribers);

    public void RemoveMetricsSubscriber()
    {
        if (Volatile.Read(ref _metricsSubscribers) > 0)
            Interlocked.Decrement(ref _metricsSubscribers);
    }

    public void AddAnalyticsSubscriber() => Interlocked.Increment(ref _analyticsSubscribers);

    public void RemoveAnalyticsSubscriber()
    {
        if (Volatile.Read(ref _analyticsSubscribers) > 0)
            Interlocked.Decrement(ref _analyticsSubscribers);
    }
}
