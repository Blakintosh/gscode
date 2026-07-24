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
        string askingPath)
    {
        List<IncludeNode> includes = new();
        foreach ( AstNode element in result.Tree.Root.Elements )
        {
            if ( element is IncludeNode includeNode )
            {
                includes.Add(includeNode);
            }
        }

        if ( includes.Count == 0 )
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

        ResolutionContext context = resolver.GetContext(askingPath);
        string extension = language == ScriptLanguage.Csc ? GameProfile.Active.ClientScriptExtension : GameProfile.Active.ServerScriptExtension;

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach ( IncludeNode includeNode in includes )
        {
            string? resolved = resolver.Resolve(context, includeNode.Path + extension);
            if ( resolved is null )
            {
                return [];
            }

            if ( !store.TryGet(PathUtil.NormalizeAbsolute(resolved), out ScriptRecord record) )
            {
                return [];
            }

            if ( IsUsed(record, calledFunctions) )
            {
                continue;
            }

            Diagnostic unused = Diagnostic.Create(
                includeNode.Range, DiagnosticSeverity.Hint, GscDiagnosticCode.UnusedInclude, includeNode.Path);

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
