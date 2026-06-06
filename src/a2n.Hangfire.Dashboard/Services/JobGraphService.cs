using System.Text.Json;
using a2n.Hangfire.Dashboard.Helpers;
using a2n.Hangfire.Dashboard.Models;
using Hangfire.Storage.Monitoring;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Builds a job dependency graph (parent ↔ children via continuations) for a given job.
/// Reads from <see cref="HangfireMonitorService"/> only — no storage-specific code.
/// </summary>
public class JobGraphService
{
    /// <summary>
    /// Maximum number of unique nodes materialized in a single graph (safety limit
    /// to bound N+1 calls against the storage backend).
    /// </summary>
    public const int DefaultMaxNodes = 30;

    /// <summary>
    /// Maximum traversal depth in either direction (ancestors or descendants).
    /// </summary>
    public const int DefaultMaxDepth = 5;

    private readonly HangfireMonitorService _monitor;

    public JobGraphService(HangfireMonitorService monitor)
    {
        _monitor = monitor;
    }

    /// <summary>
    /// Builds the dependency graph rooted at the topmost ancestor reachable from <paramref name="jobId"/>
    /// (walking up via Awaiting state's ParentId), then expanding all descendants via Continuations.
    /// Returns null when the job itself cannot be loaded.
    /// </summary>
    public JobDependencyGraph Build(string jobId, int maxNodes = DefaultMaxNodes, int maxDepth = DefaultMaxDepth)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return null;

        var details = SafeGetJobDetails(jobId);
        if (details is null) return null;

        var visited = new Dictionary<string, JobGraphNode>(StringComparer.Ordinal);
        var ctx = new BuildContext { MaxNodes = maxNodes, MaxDepth = maxDepth };

        // 1. Walk up to the topmost ancestor.
        var rootId = WalkToRoot(jobId, details, ctx);

        // 2. Materialize the root and expand downward.
        var rootDetails = rootId == jobId ? details : SafeGetJobDetails(rootId);
        var rootNode = MaterializeNode(rootId, rootDetails, visited, ctx);
        rootNode.IsCurrent = string.Equals(rootId, jobId, StringComparison.Ordinal);

        ExpandDescendants(rootNode, rootDetails, jobId, visited, ctx, depth: 0);

        return new JobDependencyGraph
        {
            Root = rootNode,
            NodeCount = visited.Count,
            LimitReached = ctx.LimitReached,
            HasEdges = HasAnyEdge(rootNode),
        };
    }

    private string WalkToRoot(string startJobId, JobDetailsDto startDetails, BuildContext ctx)
    {
        var current = startJobId;
        var details = startDetails;
        var seen = new HashSet<string>(StringComparer.Ordinal) { startJobId };

        for (var i = 0; i < ctx.MaxDepth; i++)
        {
            var parentId = ExtractParentIdFromHistory(details);
            if (string.IsNullOrEmpty(parentId)) return current;
            if (!seen.Add(parentId)) return current; // cycle guard

            var parentDetails = SafeGetJobDetails(parentId);
            if (parentDetails is null) return current; // parent expired — stop here, current becomes root

            current = parentId;
            details = parentDetails;
        }

        // Depth limit hit while walking up — current is the highest ancestor we could reach.
        ctx.LimitReached = true;
        return current;
    }

    private void ExpandDescendants(
        JobGraphNode node,
        JobDetailsDto nodeDetails,
        string focusedJobId,
        Dictionary<string, JobGraphNode> visited,
        BuildContext ctx,
        int depth)
    {
        if (nodeDetails is null) return;

        if (depth >= ctx.MaxDepth)
        {
            // Mark this node so the UI can render an ellipsis indicator.
            if (HasContinuations(nodeDetails))
            {
                node.Truncated = true;
                ctx.LimitReached = true;
            }
            return;
        }

        var continuations = ParseContinuations(nodeDetails);
        if (continuations.Count == 0) return;

        foreach (var entry in continuations)
        {
            if (string.IsNullOrEmpty(entry.JobId)) continue;

            // Reuse already-materialized node (cycle / diamond guard).
            if (visited.TryGetValue(entry.JobId, out var existing))
            {
                node.Children.Add(new JobGraphEdge { Condition = entry.Options, Target = existing });
                continue;
            }

            if (visited.Count >= ctx.MaxNodes)
            {
                node.Truncated = true;
                ctx.LimitReached = true;
                return;
            }

            var childDetails = SafeGetJobDetails(entry.JobId);
            var childNode = MaterializeNode(entry.JobId, childDetails, visited, ctx);
            childNode.IsCurrent = string.Equals(entry.JobId, focusedJobId, StringComparison.Ordinal);

            node.Children.Add(new JobGraphEdge { Condition = entry.Options, Target = childNode });

            ExpandDescendants(childNode, childDetails, focusedJobId, visited, ctx, depth + 1);
        }
    }

    private static JobGraphNode MaterializeNode(
        string jobId,
        JobDetailsDto details,
        Dictionary<string, JobGraphNode> visited,
        BuildContext _)
    {
        var node = new JobGraphNode { JobId = jobId };

        if (details is null)
        {
            node.NotFound = true;
            node.DisplayName = $"#{jobId}";
        }
        else
        {
            node.DisplayName = JobNameHelper.GetDisplayName(details.Job, details.InvocationData);
            node.StateName = details.History?.FirstOrDefault()?.StateName;
        }

        visited[jobId] = node;
        return node;
    }

    private JobDetailsDto SafeGetJobDetails(string jobId)
    {
        try
        {
            return _monitor.GetJobDetails(jobId);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Looks at the job's state history and returns the ParentId stored on any Awaiting state entry.
    /// History order is implementation-defined across storages, so we scan all entries.
    /// </summary>
    private static string ExtractParentIdFromHistory(JobDetailsDto details)
    {
        if (details?.History is null) return null;

        foreach (var entry in details.History)
        {
            if (!string.Equals(entry.StateName, "Awaiting", StringComparison.Ordinal)) continue;
            if (entry.Data is null) continue;
            if (entry.Data.TryGetValue("ParentId", out var parentId) && !string.IsNullOrEmpty(parentId))
            {
                return parentId;
            }
        }

        return null;
    }

    private static bool HasContinuations(JobDetailsDto details)
    {
        if (details?.Properties is null) return false;
        return details.Properties.TryGetValue("Continuations", out var raw)
               && !string.IsNullOrWhiteSpace(raw)
               && raw.Trim() != "[]";
    }

    /// <summary>
    /// Deserializes the "Continuations" job parameter. Hangfire serializes it as a JSON
    /// array of <c>{ "JobId": "...", "Options": 0 }</c>. We use System.Text.Json with
    /// case-insensitive property mapping to avoid pulling Newtonsoft into this project,
    /// and to keep the format independent of internal Hangfire types.
    /// </summary>
    private static List<ParsedContinuation> ParseContinuations(JobDetailsDto details)
    {
        if (!HasContinuations(details)) return new List<ParsedContinuation>();
        var raw = details.Properties["Continuations"];

        try
        {
            var entries = JsonSerializer.Deserialize<List<ContinuationDto>>(raw, JsonOpts) ?? new();
            var result = new List<ParsedContinuation>(entries.Count);
            foreach (var e in entries)
            {
                if (string.IsNullOrEmpty(e.JobId)) continue;
                result.Add(new ParsedContinuation
                {
                    JobId = e.JobId,
                    Options = MapOptions(e.Options),
                });
            }
            return result;
        }
        catch
        {
            return new List<ParsedContinuation>();
        }
    }

    private static JobContinuationCondition MapOptions(int raw) => raw switch
    {
        1 => JobContinuationCondition.OnlyOnSucceededState,
        2 => JobContinuationCondition.OnlyOnDeletedState,
        _ => JobContinuationCondition.OnAnyFinishedState,
    };

    private static bool HasAnyEdge(JobGraphNode node)
    {
        if (node?.Children is null) return false;
        if (node.Children.Count > 0) return true;
        foreach (var edge in node.Children)
        {
            if (HasAnyEdge(edge.Target)) return true;
        }
        return false;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class ContinuationDto
    {
        public string JobId { get; set; }
        public int Options { get; set; }
    }

    private struct ParsedContinuation
    {
        public string JobId { get; set; }
        public JobContinuationCondition Options { get; set; }
    }

    private sealed class BuildContext
    {
        public int MaxNodes;
        public int MaxDepth;
        public bool LimitReached;
    }
}
