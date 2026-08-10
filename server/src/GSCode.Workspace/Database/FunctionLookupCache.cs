using System.Collections.Immutable;

namespace GSCode.Workspace.Database;

/// <summary>
/// A memo over <see cref="DatabaseQueries.LookupFunctions"/> for the span of ONE file's analysis.
///
/// The lints that call it do so per REFERENCE, and a script calls the same handful of names over
/// and over — <c>flag_wait</c>, <c>isdefined</c>, a namespace's own helpers — so the same question
/// is asked dozens of times per file. The declaration index made each answer cheap; this stops
/// asking for it repeatedly, including the array allocation each answer would otherwise make.
///
/// Deliberately per FILE and thrown away with it. A longer-lived cache would have to be invalidated
/// on every edit anywhere in the workspace, since an unqualified call under a merge dialect resolves
/// by name across everything indexed — that is a subscription problem, and this is a dictionary.
///
/// The key omits the asking context, path and namespaces because they are fixed for the file being
/// analysed; construct one cache per file and per store, which is what every caller does.
/// </summary>
public sealed class FunctionLookupCache
{
    private readonly Dictionary<(string? Namespace, string Name, bool IncludePrivate), ImmutableArray<ResolvedFunction>> _memo = [];

    private readonly LanguageStore _store;
    private readonly string _askingContextId;
    private readonly string _askingPath;
    private readonly ImmutableArray<string> _askingNamespaces;

    public FunctionLookupCache(
        LanguageStore store,
        string askingContextId,
        string askingPath,
        ImmutableArray<string> askingNamespaces = default)
    {
        _store = store;
        _askingContextId = askingContextId;
        _askingPath = askingPath;
        _askingNamespaces = askingNamespaces;
    }

    /// <summary><see cref="DatabaseQueries.LookupFunctions"/>, answered once per distinct question.</summary>
    public ImmutableArray<ResolvedFunction> Lookup(string? namespaceName, string keyName, bool includePrivate = false)
    {
        (string? Namespace, string Name, bool IncludePrivate) key = (namespaceName, keyName, includePrivate);
        if ( _memo.TryGetValue(key, out ImmutableArray<ResolvedFunction> cached) )
        {
            return cached;
        }

        ImmutableArray<ResolvedFunction> found = DatabaseQueries.LookupFunctions(
            _store, _askingContextId, _askingPath, namespaceName, keyName, includePrivate, _askingNamespaces);

        _memo[key] = found;
        return found;
    }
}
