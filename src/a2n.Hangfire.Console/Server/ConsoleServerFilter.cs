using Hangfire.Common;
using Hangfire.Console.Serialization;
using Hangfire.Console.Storage;
using Hangfire.Server;
using Hangfire.States;

namespace Hangfire.Console.Server;

/// <summary>
/// Server filter to initialize and cleanup console environment.
/// </summary>
internal class ConsoleServerFilter : IServerFilter
{
    private readonly ConsoleOptions _options;

    public ConsoleServerFilter(ConsoleOptions options)
    {
        _options = options;
    }

    public void OnPerforming(PerformingContext filterContext)
    {
        var state = filterContext.Connection.GetStateData(filterContext.BackgroundJob.Id);

        if (state is null) return;
        if (!string.Equals(state.Name, ProcessingState.StateName, StringComparison.OrdinalIgnoreCase)) return;

        var startedAt = JobHelper.DeserializeDateTime(state.Data["StartedAt"]);

        filterContext.Items["ConsoleContext"] = new ConsoleContext(
            new ConsoleId(filterContext.BackgroundJob.Id, startedAt),
            new ConsoleStorage(filterContext.Connection),
            _options);
    }

    public void OnPerformed(PerformedContext filterContext)
    {
        var context = ConsoleContext.FromPerformContext(filterContext);
        if (context is null) return;

        if (_options.FollowJobRetentionPolicy)
            context.FixExpiration();
        else
            context.Expire(_options.ExpireIn);

        filterContext.Items.Remove("ConsoleContext");
    }
}
