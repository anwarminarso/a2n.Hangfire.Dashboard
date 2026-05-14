using System.Reflection;
using Hangfire.Client;
using Hangfire.Tags.Attributes;
using Hangfire.Tags.Storage;

namespace Hangfire.Tags.States;

/// <summary>
/// Client filter that automatically adds tags from [Tag] attributes when a job is created.
/// </summary>
internal class CreateJobFilter : IClientFilter
{
    private readonly TagsOptions _options;

    public CreateJobFilter(TagsOptions options)
    {
        _options = options;
    }

    public void OnCreating(CreatingContext filterContext)
    {
    }

    public void OnCreated(CreatedContext filterContext)
    {
        if (filterContext.BackgroundJob?.Id is null)
            return;

        var mi = filterContext.Job.Method;

        // Handle inherited types
        if (filterContext.Job.Method.DeclaringType != filterContext.Job.Type)
        {
            var dmi = filterContext.Job.Type.GetMethod(
                filterContext.Job.Method.Name,
                filterContext.Job.Method.GetParameters().Select(p => p.ParameterType).ToArray());
            mi = dmi ?? mi;
        }

        // Collect tags from method, declaring type, and job type
        var attrs = mi.GetCustomAttributes<TagAttribute>()
            .Concat(filterContext.Job.Type?.GetCustomAttributes<TagAttribute>() ?? [])
            .Concat(mi.DeclaringType?.GetCustomAttributes<TagAttribute>() ?? [])
            .Select(t => t.Tag)
            .ToList();

        if (attrs.Count == 0)
            return;

        // Format tags with job arguments (supports string.Format patterns)
        var args = filterContext.Job.Args.ToArray();
        var tags = attrs
            .Select(tag =>
            {
                try { return string.Format(tag, args); }
                catch { return tag; }
            })
            .Where(t => !string.IsNullOrEmpty(t));

        using var storage = new TagsStorage(JobStorage.Current, _options);
        storage.AddTags(filterContext.BackgroundJob.Id, tags);
    }
}
