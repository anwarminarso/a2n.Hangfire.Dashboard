using Hangfire.Tags;

// ReSharper disable once CheckNamespace
namespace Hangfire
{
    /// <summary>
    /// Provides extension methods to setup Hangfire.Tags.
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

            // TODO: Register state filters
            // TODO: Register storage

            return configuration;
        }
    }
}
