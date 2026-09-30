using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Server.Handlers;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;

namespace GSCode.Server.Formatting;

/// <summary>
/// The spelling a call should be written with, for <see cref="FormatOptions.FixCasing"/>: its
/// script function's declared spelling, or failing that its builtin's documented one. Answers null
/// whenever the spelling is not safe to change, and the call is then left as written.
/// </summary>
/// <remarks>
/// A name that is BOTH a script function and a builtin answers null. Spelling is what picks between
/// the two — stock declares <c>function earthquake()</c> and, in the same file, calls the engine's
/// <c>Earthquake( … )</c> — so recasing either call would change which function runs.
/// </remarks>
public sealed class CallCasing
{
    /// <summary>
    /// Enough declarations to see whether they disagree on a spelling. A bare name can have
    /// thousands of declarations on a merge dialect; two differing spellings already decide it.
    /// </summary>
    private const int DeclarationLimit = 16;

    private readonly NavigationTarget _target;
    private readonly BuiltinApi _builtins;
    private readonly Dictionary<string, string?> _answers = new(StringComparer.Ordinal);

    public CallCasing(NavigationTarget target, BuiltinApiSet builtins)
    {
        _target = target;
        _builtins = builtins.For(target.Language);
    }

    /// <summary>The spelling for a call to <paramref name="name"/>, qualified or not, or null.</summary>
    public string? SpellingFor(string? qualifier, string name)
    {
        string? namespaceKey = qualifier?.ToLowerInvariant();
        string nameKey = name.ToLowerInvariant();
        string cacheKey = (namespaceKey ?? "") + "::" + nameKey;
        if ( _answers.TryGetValue(cacheKey, out string? known) )
        {
            return known;
        }

        string? answer = Resolve(namespaceKey, nameKey);
        _answers[cacheKey] = answer;
        return answer;
    }

    private string? Resolve(string? namespaceKey, string nameKey)
    {
        // `sys::name` is the explicit builtin form; no script declaration can claim it.
        if ( namespaceKey == "sys" )
        {
            return _builtins.Find(nameKey)?.Name;
        }

        HashSet<string> declared = DeclaredSpellings(namespaceKey, nameKey);

        // A qualified call names a namespace or a class, never a builtin.
        BuiltinFunction? builtin = namespaceKey is null ? _builtins.Find(nameKey) : null;

        if ( declared.Count > 0 && builtin is not null )
        {
            return null;
        }

        if ( declared.Count == 1 )
        {
            return declared.First();
        }

        if ( declared.Count > 1 )
        {
            return null;
        }

        return builtin?.Name;
    }

    /// <summary>
    /// The distinct spellings the name is declared with: this file's own declarations, which may be
    /// newer than the index, and every visible one the index knows.
    /// </summary>
    private HashSet<string> DeclaredSpellings(string? namespaceKey, string nameKey)
    {
        HashSet<string> spellings = new(StringComparer.Ordinal);
        foreach ( FunctionSymbol function in _target.Result.Extraction.Functions )
        {
            bool sameName = string.Equals(function.KeyName, nameKey, StringComparison.Ordinal);
            bool sameNamespace = namespaceKey is null
                || string.Equals(function.Namespace, namespaceKey, StringComparison.Ordinal);
            if ( sameName && sameNamespace )
            {
                spellings.Add(function.Name);
            }
        }

        ImmutableArray<ResolvedFunction> functions = DatabaseQueries.LookupFunctions(
            _target.Store, _target.ContextId, _target.Path, namespaceKey, nameKey,
            askingNamespaces: _target.Namespaces, limit: DeclarationLimit);
        foreach ( ResolvedFunction resolved in functions )
        {
            spellings.Add(resolved.Function.Name);
        }

        return spellings;
    }
}
