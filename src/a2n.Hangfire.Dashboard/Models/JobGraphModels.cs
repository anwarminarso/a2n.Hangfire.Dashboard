using System.Collections.Generic;

namespace a2n.Hangfire.Dashboard.Models;

/// <summary>
/// Continuation condition options. Mirrors <c>Hangfire.JobContinuationOptions</c>
/// without forcing a dependency on the internal Continuation struct.
/// </summary>
public enum JobContinuationCondition
{
    OnAnyFinishedState = 0,
    OnlyOnSucceededState = 1,
    OnlyOnDeletedState = 2,
}

/// <summary>
/// A single node in the job dependency graph (parent ↔ child via continuations).
/// </summary>
public class JobGraphNode
{
    public string JobId { get; set; }

    /// <summary>Resolved display name, or fallback to InvocationData when assembly unavailable.</summary>
    public string DisplayName { get; set; }

    /// <summary>Latest state name (e.g. "Succeeded", "Awaiting", "Failed"). Null if job not found.</summary>
    public string StateName { get; set; }

    /// <summary>True when the job referenced by an edge could not be loaded (expired or deleted).</summary>
    public bool NotFound { get; set; }

    /// <summary>True when this node is the job currently being viewed (highlighted in UI).</summary>
    public bool IsCurrent { get; set; }

    /// <summary>True when the traversal depth/node limit was hit and the children of this node were not expanded.</summary>
    public bool Truncated { get; set; }

    /// <summary>Child continuations of this job.</summary>
    public List<JobGraphEdge> Children { get; set; } = new();
}

/// <summary>
/// Edge between two graph nodes. The <see cref="Condition"/> indicates the continuation
/// option (OnSucceeded / OnDeleted / OnAnyFinishedState).
/// </summary>
public class JobGraphEdge
{
    public JobContinuationCondition Condition { get; set; }
    public JobGraphNode Target { get; set; }
}

/// <summary>
/// Result of a graph build operation rooted at the requested job.
/// The graph is rendered top-down: <see cref="Root"/> is the top-most ancestor
/// (or the requested job itself if it has no parent).
/// </summary>
public class JobDependencyGraph
{
    public JobGraphNode Root { get; set; }

    /// <summary>True when at least one continuation edge was found (graph is non-trivial).</summary>
    public bool HasEdges { get; set; }

    /// <summary>Total number of nodes materialized (including the current job).</summary>
    public int NodeCount { get; set; }

    /// <summary>True when traversal stopped early due to depth/node limits.</summary>
    public bool LimitReached { get; set; }
}
