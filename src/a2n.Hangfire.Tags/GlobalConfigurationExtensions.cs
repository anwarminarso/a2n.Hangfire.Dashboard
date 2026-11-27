using Hangfire.Tags;
using Hangfire.Tags.States;

// ReSharper disable once CheckNamespace
namespace Hangfire;

/// <summary>
/// Provides extension methods to setup Hangfire.Tags.
/// API-compatible with the original Hangfire.Tags package.
/// </summary>
public static class TagsGlobalConfigurationExtensions
{
    /// <summary>
    /// Configures Hangfire to use Tags.
    /// </summary>
    /// <param name="configuration">Global configuration</param>
    /// <param name="options">Options for tags</param>
    public static IGlobalConfiguration UseTags(
        this IGlobalConfiguration configuration,
        TagsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        options ??= new TagsOptions();

        // Register client filter (auto-tag from [Tag] attributes on job creation)
        GlobalJobFilters.Filters.Add(new CreateJobFilter(options), int.MaxValue);

        // Register state filter (manage tag expiration on state changes)
        GlobalJobFilters.Filters.Add(new TagsCleanupStateFilter(options), int.MaxValue);

        return configuration;
    }
}
