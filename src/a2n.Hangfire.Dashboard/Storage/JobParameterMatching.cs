using Hangfire.Common;

namespace a2n.Hangfire.Dashboard.Storage;

/// <summary>
/// Hangfire persists <see cref="CreateContext.Parameters"/> with <see cref="SerializationOption.User"/> (JSON).
/// Recurring job ids in Set/Hash storage are plain strings — SQL must match both forms.
/// </summary>
public static class JobParameterMatching
{
    public static string SerializeUserString(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return SerializationHelper.Serialize(value, typeof(string), SerializationOption.User);
    }

    /// <summary>Plain and JSON-serialized values for SQL IN / ANY clauses.</summary>
    public static string[] AllValueForms(IEnumerable<string> plainValues)
    {
        if (plainValues is null)
            return Array.Empty<string>();

        var forms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plain in plainValues)
        {
            if (string.IsNullOrEmpty(plain))
                continue;

            forms.Add(plain);
            forms.Add(SerializeUserString(plain));
        }

        return forms.ToArray();
    }

    /// <summary>
    /// Maps stored <c>jobparameter.value</c> (plain or JSON) to the recurring job id from storage sets.
    /// </summary>
    public static Dictionary<string, string> BuildStoredValueToPlainIdLookup(IEnumerable<string> plainIds)
    {
        var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
        if (plainIds is null)
            return lookup;

        foreach (var plain in plainIds)
        {
            if (string.IsNullOrEmpty(plain))
                continue;

            lookup[plain] = plain;

            var serialized = SerializeUserString(plain);
            if (!string.IsNullOrEmpty(serialized))
                lookup[serialized] = plain;
        }

        return lookup;
    }

    /// <summary>
    /// Resolves a stored recurring job id (plain or JSON) to the known plain id for display and matching.
    /// </summary>
    /// <param name="storedValue"></param>
    /// <param name="knownPlainIds"></param>
    /// <returns></returns>
    public static string ResolvePlainRecurringJobId(string storedValue, IReadOnlyList<string> knownPlainIds)
    {
        if (string.IsNullOrEmpty(storedValue))
            return storedValue;

        var lookup = BuildStoredValueToPlainIdLookup(knownPlainIds);
        return ResolvePlainRecurringJobId(storedValue, lookup);
    }


    /// <summary>
    /// Resolves a stored recurring job id (plain or JSON) to the known plain id for display and matching using a pre-built lookup.
    /// </summary>
    /// <param name="storedValue"></param>
    /// <param name="storedValueToPlainId"></param>
    /// <returns></returns>
    public static string ResolvePlainRecurringJobId(
        string storedValue,
        IReadOnlyDictionary<string, string> storedValueToPlainId)
    {
        if (string.IsNullOrEmpty(storedValue))
            return storedValue;

        if (storedValueToPlainId is not null
            && storedValueToPlainId.TryGetValue(storedValue, out var plain))
            return plain;

        try
        {
            var deserialized = SerializationHelper.Deserialize<string>(storedValue, SerializationOption.User);
            if (!string.IsNullOrEmpty(deserialized))
                return deserialized;
        }
        catch
        {
            // Not JSON — return as stored.
        }

        return storedValue;
    }
}
