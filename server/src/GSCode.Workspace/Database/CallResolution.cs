using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;

namespace GSCode.Workspace.Database;

/// <summary>
/// Which function a written call names, for the two features that ask about a call site rather
/// than about a symbol: signature help and the parameter-name inlay hints.
///
/// They had a resolver each, and the resolvers drifted. Signature help split on
/// <see cref="GameProfile.ResolvesByNamespace"/> because a merge dialect reaches another file
/// through <c>#include</c> rather than through a namespace; the hint path did not, so on CoD4, WaW,
/// MW2 and BO1 it answered only for the asking file's own functions and was silent on every
/// cross-file call. That is the kind of divergence two copies produce: not a disagreement anyone
/// decided on, but a rule added to one of them.
///
/// Only the shared half lives here. What each feature does with the answer differs — one builds a
/// label with an active parameter, the other wants the names — and so does how each finds the call
/// in the first place: signature help is TOKEN-driven, so it works on an argument list that has no
/// tree yet, while the hints walk the tree. Folding those together would change what each feature
/// can do, rather than stop them disagreeing about the same question.
///
/// One disagreement is LEFT, on purpose and written down rather than quietly settled. For
/// <c>Foo::bar( ... )</c> where <c>Foo</c> names both a namespace and a class, signature help tries
/// the class first (<c>SignatureEngine.MethodsFor</c>, before its namespace lookup) and the hints
/// try the namespace first (<c>InlayHintHandler.QualifiedParameterNames</c>, which says so). Both
/// orders are defensible and picking one here would change a shipped feature's answers as a side
/// effect of a refactor. BO3 ships three such names out of 427 namespaces and 37 classes:
/// <c>phalanx</c>, <c>robotphalanx</c> and <c>throttle</c>.
/// </summary>
public static class CallResolution
{
    /// <summary>
    /// The class whose body contains this position, over the file's own handful of classes. A bare
    /// name inside a class body means a method first, so both callers ask this before any namespace
    /// or builtin lookup.
    /// </summary>
    public static string? EnclosingClassAt(ParseResult result, Position position)
    {
        foreach ( ClassSymbol classSymbol in result.Extraction.Classes )
        {
            if ( classSymbol.FullRange.Contains(position) )
            {
                return classSymbol.KeyName;
            }
        }

        return null;
    }

    /// <summary>
    /// The script function a BARE name resolves to from this file, or null when the scripts declare
    /// none. Builtins are deliberately not consulted: the callers present an engine function
    /// differently enough that each keeps its own fallback.
    /// </summary>
    /// <remarks>
    /// The dialect split is the whole point of this method. A merge dialect (<c>#include</c>:
    /// CoD4/WaW/MW2/BO1) has no <c>#namespace</c>, so <c>SymbolExtractor</c> defaults every
    /// function's namespace to its FILE NAME STEM — a resolution fallback that names no scope
    /// anybody wrote. Asking by declared namespace there answers for the asking file's OWN
    /// functions and for nothing else, which is why a feature that got this wrong still looked like
    /// it worked.
    /// </remarks>
    public static FunctionSymbol? UnqualifiedFunction(
        LanguageStore store,
        string askingContextId,
        ParseResult result,
        string keyName,
        GameProfile? profile = null)
    {
        if ( !(profile ?? GameProfile.Active).ResolvesByNamespace )
        {
            // The bounded lookup rather than the whole scope: this is asked per call site by the
            // hints and per keystroke by signature help, and building every function the scope
            // offers in order to read one of them is the cost, not the match.
            return DatabaseQueries.FunctionInIncludeScope(
                store, askingContextId, result.FilePath, DatabaseQueries.IncludedScriptPaths(result), keyName);
        }

        // Each namespace the file participates in, in order, built once rather than per iteration,
        // and from the declarations: a namespace span list includes a phantom leading span whose
        // lookup scans the whole store to return nothing.
        ImmutableArray<string> askingNamespaces = DatabaseQueries.DeclaredNamespaces(result);

        foreach ( string declared in askingNamespaces )
        {
            ImmutableArray<ResolvedFunction> found = DatabaseQueries.LookupFunctions(
                store, askingContextId, result.FilePath, declared, keyName, askingNamespaces: askingNamespaces);

            if ( found.Length > 0 )
            {
                return found[0].Function;
            }
        }

        return null;
    }

    /// <summary>
    /// The function a <c>maps\_utility::set_ambient( ... )</c> reference names — the Infinity Ward
    /// path form, which only the merge dialects have.
    ///
    /// The path names the FILE rather than a namespace, so this is a lookup by name scoped to that
    /// one file. Asked with an EMPTY asking path on purpose: the scope helper searches the asking
    /// file first otherwise, and a path call names where it wants to go.
    /// </summary>
    public static FunctionSymbol? PathQualifiedFunction(
        LanguageStore store, string askingContextId, string writtenPath, string keyName)
    {
        return DatabaseQueries.FunctionInIncludeScope(
            store, askingContextId, askingPath: "", [RelativePathIndex.Normalize(writtenPath)], keyName);
    }
}
