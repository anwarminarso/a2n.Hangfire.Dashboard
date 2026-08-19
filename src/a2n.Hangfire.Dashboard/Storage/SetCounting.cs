using Hangfire.Storage;

namespace a2n.Hangfire.Dashboard.Storage;

/// <summary>
/// Counts set members without requiring the storage to implement Hangfire's extended API.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="JobStorageConnection.GetSetCount(string)"/> is <c>virtual</c>, not <c>abstract</c>, and
/// its base implementation throws <see cref="NotSupportedException"/> for the
/// <c>Storage.ExtendedApi</c> feature. Hangfire.SqlServer and Hangfire.PostgreSql override it, but a
/// storage is not required to, so calling it directly makes the dashboard depend on an optional
/// capability.
/// </para>
/// <para>
/// That mattered most in <c>NavMenu</c>, which is part of the layout: an unhandled throw there fails
/// the circuit and takes down the whole shell rather than one counter. This falls back to
/// <see cref="JobStorageConnection.GetAllItemsFromSet(string)"/>, which is <c>abstract</c> and so is
/// implemented by every storage. The fallback reads the members instead of counting them, which is
/// more work, but it only runs on storages that cannot count at all.
/// </para>
/// </remarks>
public static class SetCounting
{
    /// <summary>
    /// Returns the number of members in the given set, or 0 when the set is missing or the storage
    /// can neither count nor list it.
    /// </summary>
    public static long Count(JobStorageConnection connection, string key)
    {
        if (connection is null || string.IsNullOrEmpty(key))
            return 0;

        try
        {
            return connection.GetSetCount(key);
        }
        catch (NotSupportedException)
        {
            // Storage does not implement the extended API — fall back to listing.
        }

        try
        {
            return connection.GetAllItemsFromSet(key)?.Count ?? 0;
        }
        catch (NotSupportedException)
        {
            return 0;
        }
    }
}
