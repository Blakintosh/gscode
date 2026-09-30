using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Server.Handlers;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;

namespace GSCode.Server.Formatting;

/// <summary>
/// The workspace's answers to <see cref="ICasingLookup"/>: a function's declared spelling or its
/// builtin's documented one, a namespace's as its <c>#namespace</c> directive writes it, and a
/// class's as its declaration does.
/// </summary>
/// <remarks>
/// A bare call resolves to a builtin before a script function of the same name, so it takes the
/// builtin's spelling. Stock shows both halves in one file: <c>exploder_shared.gsc</c> declares
/// <c>function earthquake()</c>, calls the engine with a bare <c>Earthquake( … )</c>, and reaches its
/// own function only as <c>exploder::earthquake()</c>. A threaded call cannot mean a builtin —
/// <c>_zm.gsc</c> threads its own zero-argument <c>spawnSpectator()</c> although the engine's needs
/// two — so it takes the script function's spelling.
/// </remarks>
public sealed class CallCasing : ICasingLookup
{
    /// <summary>
    /// Enough declarations to see whether they disagree on a spelling. A bare name or a busy
    /// namespace can have thousands; two differing spellings already decide it.
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

    public string? Function(string? qualifier, string name, bool preferScript)
    {
        string? namespaceKey = qualifier?.ToLowerInvariant();
        string nameKey = name.ToLowerInvariant();
        string cacheKey = "f:" + (preferScript ? "s:" : "b:") + (namespaceKey ?? "") + "::" + nameKey;
        if ( _answers.TryGetValue(cacheKey, out string? known) )
        {
            return known;
        }

        string? answer = ResolveFunction(namespaceKey, nameKey, preferScript);
        _answers[cacheKey] = answer;
        return answer;
    }

    public string? Qualifier(string name)
    {
        string key = name.ToLowerInvariant();
        string cacheKey = "q:" + key;
        if ( _answers.TryGetValue(cacheKey, out string? known) )
        {
            return known;
        }

        HashSet<string> spellings = NamespaceSpellings(key);
        spellings.UnionWith(ClassSpellings(key));
        string? answer = spellings.Count == 1 ? spellings.First() : null;
        _answers[cacheKey] = answer;
        return answer;
    }

    public string? Class(string name)
    {
        string key = name.ToLowerInvariant();
        string cacheKey = "c:" + key;
        if ( _answers.TryGetValue(cacheKey, out string? known) )
        {
            return known;
        }

        HashSet<string> spellings = ClassSpellings(key);
        string? answer = spellings.Count == 1 ? spellings.First() : null;
        _answers[cacheKey] = answer;
        return answer;
    }

    private string? ResolveFunction(string? namespaceKey, string nameKey, bool preferScript)
    {
        // `sys::name` is the explicit builtin form; no script declaration can claim it.
        if ( namespaceKey == "sys" )
        {
            return _builtins.Find(nameKey)?.Name;
        }

        HashSet<string> declared = FunctionSpellings(namespaceKey, nameKey);
        string? script = declared.Count == 1 ? declared.First() : null;

        // A qualified call names a namespace or a class, never a builtin.
        if ( namespaceKey is not null )
        {
            return script;
        }

        BuiltinFunction? builtin = _builtins.Find(nameKey);
        if ( builtin is null )
        {
            return script;
        }

        if ( !preferScript || declared.Count == 0 )
        {
            return builtin.Name;
        }

        return script;
    }

    /// <summary>
    /// The distinct spellings a function is declared with: this file's own declarations, which may be
    /// newer than the index, and every visible one the index knows.
    /// </summary>
    private HashSet<string> FunctionSpellings(string? namespaceKey, string nameKey)
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

    /// <summary>How the <c>#namespace</c> directives declaring this namespace spell it.</summary>
    private HashSet<string> NamespaceSpellings(string namespaceKey)
    {
        HashSet<string> spellings = new(StringComparer.Ordinal);
        AddDirectiveSpellings(_target.Result.Extraction.Namespaces, namespaceKey, spellings);

        int read = 0;
        foreach ( string path in _target.Store.FilesDeclaringInto(namespaceKey) )
        {
            if ( read++ >= DeclarationLimit )
            {
                break;
            }

            if ( _target.Store.TryGet(path, out ScriptRecord record) )
            {
                AddDirectiveSpellings(record.Namespaces, namespaceKey, spellings);
            }
        }

        return spellings;
    }

    private static void AddDirectiveSpellings(
        ImmutableArray<NamespaceSpan> spans, string namespaceKey, HashSet<string> spellings)
    {
        foreach ( NamespaceSpan span in spans )
        {
            // Only a span a directive opened has an author's spelling; the file-default one is a key.
            bool fromDirective = span.NameRange != Core.Text.TextRange.Empty;
            if ( fromDirective && string.Equals(span.KeyName, namespaceKey, StringComparison.Ordinal) )
            {
                spellings.Add(span.Name);
            }
        }
    }

    /// <summary>How the classes of this name are declared, here and anywhere visible.</summary>
    private HashSet<string> ClassSpellings(string classKey)
    {
        HashSet<string> spellings = new(StringComparer.Ordinal);
        foreach ( ClassSymbol classSymbol in _target.Result.Extraction.Classes )
        {
            if ( string.Equals(classSymbol.KeyName, classKey, StringComparison.Ordinal) )
            {
                spellings.Add(classSymbol.Name);
            }
        }

        foreach ( ResolvedClass resolved in DatabaseQueries.LookupClasses(_target.Store, _target.ContextId, null, classKey) )
        {
            spellings.Add(resolved.Class.Name);
        }

        return spellings;
    }
}
