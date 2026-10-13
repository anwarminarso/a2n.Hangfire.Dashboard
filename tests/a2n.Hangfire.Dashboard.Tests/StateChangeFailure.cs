using System;
using System.Collections.Generic;
using Hangfire.States;
using Hangfire.Storage;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Makes the state change of one chosen job throw, the way a storage error would surface in the
/// middle of a batch. A test storage adds <see cref="HandlersFor"/> to its state handlers: Hangfire's
/// state machine applies them inside the state change, so the change of that job throws and isn't
/// committed, while every other job changes as usual. Picking the job by id keeps tests independent
/// of how many connections Hangfire, or the audit log, opens along the way.
/// </summary>
internal sealed class StateChangeFailure
{
    private volatile string _jobId;

    /// <summary>Makes the next state change of this job, to any of the handled states, throw.</summary>
    public void FailJob(string jobId) => _jobId = jobId;

    /// <summary>Handlers for the states the dashboard's bulk actions move jobs to.</summary>
    public IEnumerable<IStateHandler> HandlersFor()
    {
        yield return new Handler(this, EnqueuedState.StateName);
        yield return new Handler(this, DeletedState.StateName);
    }

    private sealed class Handler : IStateHandler
    {
        private readonly StateChangeFailure _owner;

        public Handler(StateChangeFailure owner, string stateName)
        {
            _owner = owner;
            StateName = stateName;
        }

        public string StateName { get; }

        public void Apply(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
            if (context.BackgroundJob.Id == _owner._jobId)
                throw new InvalidOperationException("The storage is unavailable.");
        }

        public void Unapply(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
        }
    }
}
