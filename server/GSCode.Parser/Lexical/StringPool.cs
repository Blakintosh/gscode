using System.Collections.Concurrent;

namespace GSCode.Parser.Lexical;

/// <summary>
/// Thread-safe string pool for deduplicating frequently used strings like identifiers.
/// This significantly reduces memory usage when parsing many files with common identifiers.
/// </summary>
public static class StringPool
{
    private static readonly ConcurrentDictionary<string, string> _pool = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns a canonical instance of the string, reducing memory when the same string appears multiple times.
    /// </summary>
    /// <param name="value">The string to intern.</param>
    /// <returns>A pooled instance of the string.</returns>
    public static string Intern(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return _pool.GetOrAdd(value, static v => v);
    }

    /// <summary>
    /// Intern using ReadOnlySpan to avoid allocating a new string if already pooled.
    /// </summary>
    /// <param name="value">The span to intern.</param>
    /// <returns>A pooled instance of the string.</returns>
    public static string Intern(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
            return string.Empty;

        // Use the new .NET 9 GetAlternateLookup or fallback to string allocation
        // For now, we'll do a simple approach that works on .NET 8
#if NET9_0_OR_GREATER
        var lookup = _pool.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(value, out string? existing))
            return existing;
#else
        // Check existing entries - for hot paths this linear scan is acceptable
        // because the dictionary is bounded by unique identifiers in the codebase
        foreach (var kvp in _pool)
        {
            if (value.SequenceEqual(kvp.Key.AsSpan()))
                return kvp.Value;
        }
#endif
        
        // Not found, allocate and add
        string newString = new(value);
        return _pool.GetOrAdd(newString, static v => v);
    }

    /// <summary>
    /// Clears the string pool. Useful for testing or when restarting analysis.
    /// </summary>
    public static void Clear() => _pool.Clear();

    /// <summary>
    /// Gets the current number of unique strings in the pool.
    /// </summary>
    public static int Count => _pool.Count;
}
