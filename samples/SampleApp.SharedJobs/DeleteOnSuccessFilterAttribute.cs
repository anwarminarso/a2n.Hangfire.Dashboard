using Hangfire.Common;
using Hangfire.States;

namespace SampleApp.SharedJobs;

/// <summary>
/// Custom Hangfire job filter that deletes a job as soon as it succeeds instead of keeping it in
/// the Succeeded list. Named to match the <c>[DeleteOnSuccessFilter]</c> attribute described in
/// issue #10 so the sample reproduces the reported job shape end to end.
/// </summary>
/// <remarks>
/// When the state machine is about to elect <see cref="SucceededState"/>, the candidate is swapped
/// for a <see cref="DeletedState"/> so the job leaves the dashboard immediately after completing.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class DeleteOnSuccessFilterAttribute : JobFilterAttribute, IElectStateFilter
{
    public void OnStateElection(ElectStateContext context)
    {
        if (context.CandidateState is SucceededState)
        {
            context.CandidateState = new DeletedState
            {
                Reason = "Deleted automatically on success (DeleteOnSuccessFilter)."
            };
        }
    }
}
