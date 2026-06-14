namespace a2n.Hangfire.Dashboard.Internal;

/// <summary>
/// Pure helper resolving the <c>Effective_Queue</c> used when creating recurring jobs and when
/// enqueuing one-off jobs. The precedence is applied identically for both paths (Req 13.6, 13.7):
/// when a <see cref="QueueAttribute"/> applies to the resolved method (reported via
/// <see cref="QueueAttributeInfo.IsPresent"/>), the attribute's queue wins; otherwise the
/// operator-supplied <c>Configured_Queue</c> is used, defaulting to <c>"default"</c> when blank.
/// </summary>
internal static class EffectiveQueue
{
    /// <summary>
    /// The Hangfire fallback queue name used when no queue is otherwise determined.
    /// </summary>
    public const string DefaultQueue = "default";

    /// <summary>
    /// Resolves the effective queue for a job (Req 13.6, 13.7).
    /// </summary>
    /// <param name="queueAttribute">
    /// QueueAttribute reporting for the resolved method or its declaring class. When this is
    /// non-null and <see cref="QueueAttributeInfo.IsPresent"/> is <c>true</c>, the attribute's
    /// <see cref="QueueAttributeInfo.QueueName"/> takes precedence (Req 13.6).
    /// </param>
    /// <param name="configuredQueue">
    /// The operator-supplied queue. Used when no QueueAttribute applies (Req 13.7). When null,
    /// empty, or whitespace, the result falls back to <see cref="DefaultQueue"/>.
    /// </param>
    /// <returns>The effective queue name; never null or blank.</returns>
    public static string Resolve(QueueAttributeInfo queueAttribute, string configuredQueue)
    {
        if (queueAttribute is { IsPresent: true })
        {
            return queueAttribute.QueueName;
        }

        return string.IsNullOrWhiteSpace(configuredQueue) ? DefaultQueue : configuredQueue;
    }
}
