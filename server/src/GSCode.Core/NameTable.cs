using System.Collections.Concurrent;

namespace GSCode.Core;

/// <summary>
/// The shared string-interning pool. Strings are the dominant memory cost of an indexed
/// workspace, and identifiers/paths repeat massively — interning makes every repeated
/// name one object and lets lookups compare references. This is deliberately NOT
/// string.Intern (which is process-lifetime and uncollectable).
/// </summary>
public sealed class NameTable
{
    /// <summary>The process-wide table used by the pipeline. Tests may create private ones.</summary>
    public static NameTable Shared { get; } = new();

    private readonly ConcurrentDictionary<string, string> _pool = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _spanLookup;

    public NameTable()
    {
        _spanLookup = _pool.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>Interns the exact text (used for display-case names and literal content).</summary>
    public string Intern(ReadOnlySpan<char> text)
    {
        if ( _spanLookup.TryGetValue(text, out string? existing) )
        {
            return existing;
        }

        string created = new(text);
        return _pool.GetOrAdd(created, created);
    }

    /// <summary>
    /// Interns the lowercase-canonical form of the text — the form every case-insensitive
    /// lookup key (identifiers, namespaces, paths) uses, so no ignore-case comparers are
    /// needed anywhere downstream.
    /// </summary>
    public string InternLower(ReadOnlySpan<char> text)
    {
        // Most names are already lowercase; avoid the copy in that common case.
        bool hasUpper = false;
        foreach ( char character in text )
        {
            if ( char.IsUpper(character) )
            {
                hasUpper = true;
                break;
            }
        }

        if ( !hasUpper )
        {
            return Intern(text);
        }

        if ( text.Length <= 256 )
        {
            Span<char> buffer = stackalloc char[text.Length];
            text.ToLowerInvariant(buffer);
            return Intern(buffer);
        }

        return Intern(text.ToString().ToLowerInvariant());
    }
}
