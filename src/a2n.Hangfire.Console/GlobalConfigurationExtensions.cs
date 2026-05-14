using Hangfire.Console;
using Hangfire.Console.Server;
using Hangfire.Console.States;

// ReSharper disable once CheckNamespace
namespace Hangfire;

/// <summary>
/// Provides extension methods to setup Hangfire.Console.
/// API-compatible with the original Hangfire.Console package.
/// </summary>
public static class ConsoleGlobalConfigurationExtensions
{
    /// <summary>
    /// Configures Hangfire to use Console.
    /// </summary>
    /// <param name="configuration">Global configuration</param>
    /// <param name="options">Options for console</param>
    public static IGlobalConfiguration UseConsole(
        this IGlobalConfiguration configuration,
        ConsoleOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        options ??= new ConsoleOptions();
        options.Validate(nameof(options));

        // Register server filter (captures console writes during job execution)
        GlobalJobFilters.Filters.Add(new ConsoleServerFilter(options));

        // Register state filter (manages console expiration)
        GlobalJobFilters.Filters.Add(new ConsoleApplyStateFilter(options), int.MaxValue);

        return configuration;
    }
}
