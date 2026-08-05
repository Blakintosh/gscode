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
/// <c>#include</c> MERGES a file's functions into this scope, so "used" is by NAME: any function the
/// target declares is called here. Deliberately conservative — deleting a working include is worse
/// than keeping a stale one — so an autoexec keeps the include (imported for its side effects), and
/// as with the other import lints one unresolvable <c>#include</c> suppresses the whole pass rather
/// than guessing.
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

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach ( ImportedFile imported in resolvedImports.Includes )
        {
            if ( IsUsed(imported.Record, calledFunctions) )
            {
                continue;
            }

            Diagnostic unused = Diagnostic.Create(
                imported.DirectiveRange, DiagnosticSeverity.Hint, GscDiagnosticCode.UnusedInclude, imported.RawPath);

            diagnostics.Add(unused with { Tags = [DiagnosticTag.Unnecessary] });
        }

        return diagnostics.ToImmutable();
    }

    private static bool IsUsed(ScriptRecord record, HashSet<string> calledFunctions)
    {
        foreach ( FunctionSymbol function in record.Functions )
        {
            // An autoexec runs on its own; including the file IS the point.
            if ( function.IsAutoexec )
            {
                return true;
            }

            if ( calledFunctions.Contains(function.KeyName) )
            {
                return true;
            }
        }

        return false;
    }
}
