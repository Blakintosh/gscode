using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Parser.Syntax.Ast;
using GSCode.Workspace.Database;
using GSCode.Workspace.Resolution;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// The Infinity Ward counterpart to <see cref="UnusedUsingLint"/>: an <c>#include</c> whose target
/// contributes nothing this file uses. Reported as a Hint tagged Unnecessary so the directive greys
/// out.
///
/// <c>#include</c> MERGES a file's functions into this scope, so "used" is by NAME. Deliberately
/// conservative — deleting a working include is worse than keeping a stale one — so an autoexec
/// anywhere it reaches keeps it (imported for its side effects), and as with the other import lints
/// one unresolvable <c>#include</c> suppresses the whole pass rather than guessing.
///
/// The test is MARGINAL, not direct, and that distinction is what stops a Hint from manufacturing an
/// Error. <c>#include</c> flattens transitively (see <see cref="DatabaseQueries.IncludeClosure"/>), so
/// a file may include a hub purely as a conduit — <c>maps\_createpath.gsc</c> reaches
/// <c>flag_init</c> through <c>maps\_utility</c> and includes nothing else. Judging a directive by
/// what its target declares ITSELF called that unused, offered "Remove", and the removal broke the
/// file: 5026 then reports the call as out of scope. A quick fix that turns working code into an
/// error is worse than either rule being wrong alone.
///
/// So an include is reported only when removing it would take nothing away: no called name that its
/// closure supplies is supplied by it ALONE. Membership in the closure is not enough, or a hub would
/// count as used whenever anything below it is — and the stock scripts are full of files that include
/// both a hub and the file under it, where the hub really is redundant. On CoD4 that distinction is
/// 33 directives for <c>maps\_utility</c> alone.
/// </summary>
public static class UnusedIncludeLint
{
    public static ImmutableArray<Diagnostic> Analyze(
        ParseResult result,
        LanguageStore store,
        ScriptLanguage language,
        PathResolver resolver,
        string askingPath,
        FileImports? imports = null)
    {
        // Resolved once per file by WorkspaceLints and shared with the other import lints; falling
        // back to resolving here keeps this callable on its own, which the tests rely on.
        FileImports resolvedImports = imports ?? FileImports.Resolve(result, store, language, resolver, askingPath);

        if ( resolvedImports.Includes.Length == 0 || !resolvedImports.Complete )
        {
            return [];
        }

        // Every function name this file calls (unqualified or by path), ignoring its own
        // definitions. Merge dialects key functions with no namespace, so the name is the whole key.
        HashSet<string> calledFunctions = new(StringComparer.Ordinal);
        foreach ( ReferenceEntry entry in result.Extraction.References )
        {
            if ( entry.Kind != ReferenceKind.Definition && entry.Key.Kind == SymbolKind.Function )
            {
                calledFunctions.Add(entry.Key.Name);
            }
        }

        // Which directives are certainly staying: the ones whose OWN declarations this file calls,
        // or that run something on import. Everything below is judged against what they already
        // supply, which is what keeps the question non-circular — asking each directive whether some
        // OTHER removable one covers for it can conclude that two directives each cover the other and
        // that both may go, and the bulk "remove all" action would then take both.
        bool[] kept = new bool[resolvedImports.Includes.Length];
        for ( int index = 0; index < resolvedImports.Includes.Length; index++ )
        {
            kept[index] = DeclaresSomethingWanted(resolvedImports.Includes[index].Record, calledFunctions);
        }

        string extension = GameProfile.Active.ExtensionFor(language);
        HashSet<string> suppliedByKept = new(StringComparer.Ordinal);
        List<HashSet<string>?> closures = [];

        for ( int index = 0; index < resolvedImports.Includes.Length; index++ )
        {
            IncludeClosure closure = DatabaseQueries.IncludeClosure(
                store, resolver, result, askingPath, extension, [resolvedImports.Includes[index].Record]);

            // A file we could not read might be the one supplying a name, so nothing is removable.
            if ( !closure.Complete )
            {
                return [];
            }

            HashSet<string> names = new(StringComparer.Ordinal);
            foreach ( ScriptRecord record in closure.Records )
            {
                foreach ( FunctionSymbol function in record.Functions )
                {
                    names.Add(function.KeyName);

                    // An autoexec anywhere it reaches runs on its own; the include IS the point, and
                    // that is as true through a chain as it is directly.
                    kept[index] |= function.IsAutoexec;
                }
            }

            closures.Add(names);
        }

        for ( int index = 0; index < closures.Count; index++ )
        {
            if ( kept[index] )
            {
                suppliedByKept.UnionWith(closures[index]!);
            }
        }

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        for ( int index = 0; index < closures.Count; index++ )
        {
            if ( kept[index] || SuppliesSomethingNothingElseDoes(closures[index]!, calledFunctions, suppliedByKept) )
            {
                continue;
            }

            ImportedFile imported = resolvedImports.Includes[index];
            Diagnostic unused = Diagnostic.Create(
                imported.DirectiveRange, DiagnosticSeverity.Hint, GscDiagnosticCode.UnusedInclude, imported.RawPath);

            diagnostics.Add(unused with { Tags = [DiagnosticTag.Unnecessary] });
        }

        return diagnostics.ToImmutable();
    }

    /// <summary>Whether the target itself declares something this file calls, or runs on import.</summary>
    private static bool DeclaresSomethingWanted(ScriptRecord record, HashSet<string> calledFunctions)
    {
        foreach ( FunctionSymbol function in record.Functions )
        {
            if ( function.IsAutoexec || calledFunctions.Contains(function.KeyName) )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether removing this directive would take away a called name that nothing certainly-kept
    /// supplies — which is exactly when removal breaks the file.
    ///
    /// Measured against the KEPT set rather than against the other candidates, so two directives
    /// covering each other cannot both be declared removable. Being conservative here costs a stale
    /// hint; being wrong costs working code.
    /// </summary>
    private static bool SuppliesSomethingNothingElseDoes(
        HashSet<string> closure, HashSet<string> calledFunctions, HashSet<string> suppliedByKept)
    {
        foreach ( string name in closure )
        {
            if ( calledFunctions.Contains(name) && !suppliedByKept.Contains(name) )
            {
                return true;
            }
        }

        return false;
    }
}
