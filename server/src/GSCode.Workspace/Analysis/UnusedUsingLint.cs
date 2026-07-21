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
/// A cross-file lint: a <c>#using</c> whose target contributes nothing this file actually
/// uses. Reported as a Hint tagged Unnecessary so the directive greys out, pairing with the
/// remove-duplicate-#using code action.
///
/// Deliberately conservative — deleting a working import is far worse than missing a stale
/// one, so three separate rules keep an import: it declares a referenced function or class,
/// it contributes a namespace some qualified reference mentions (namespace merging means the
/// called function may live in a sibling file), or it declares an autoexec function (the file
/// is imported purely for its side effects). As with the namespace lint, one unresolvable
/// <c>#using</c> suppresses the whole pass.
/// </summary>
public static class UnusedUsingLint
{
    public static ImmutableArray<Diagnostic> Analyze(
        ParseResult result,
        LanguageStore store,
        ScriptLanguage language,
        PathResolver resolver,
        string askingPath)
    {
        List<UsingNode> usings = new();
        foreach ( AstNode element in result.Tree.Root.Elements )
        {
            if ( element is UsingNode usingNode )
            {
                usings.Add(usingNode);
            }
        }

        if ( usings.Count == 0 )
        {
            return [];
        }

        // What this file actually reaches for: qualified namespaces, and function/class keys.
        HashSet<string> referencedNamespaces = new(StringComparer.Ordinal);
        HashSet<string> referencedFunctions = new(StringComparer.Ordinal);
        HashSet<string> referencedClasses = new(StringComparer.Ordinal);
        CollectReferences(result, referencedNamespaces, referencedFunctions, referencedClasses);

        ResolutionContext context = resolver.GetContext(askingPath);
        string extension = language == ScriptLanguage.Csc ? ".csc" : ".gsc";

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach ( UsingNode usingNode in usings )
        {
            string? resolved = resolver.Resolve(context, usingNode.Path + extension);
            if ( resolved is null )
            {
                return [];
            }

            string normalized = PathUtil.NormalizeAbsolute(resolved);
            if ( !store.TryGet(normalized, out ScriptRecord record) )
            {
                return [];
            }

            if ( IsUsed(record, referencedNamespaces, referencedFunctions, referencedClasses) )
            {
                continue;
            }

            Diagnostic unused = Diagnostic.Create(
                usingNode.Range, DiagnosticSeverity.Hint, GscDiagnosticCode.UnusedUsing, usingNode.Path);

            diagnostics.Add(unused with { Tags = [DiagnosticTag.Unnecessary] });
        }

        return diagnostics.ToImmutable();
    }

    /// <summary>Gathers every namespace, function and class the file refers to, ignoring its own declarations.</summary>
    private static void CollectReferences(
        ParseResult result,
        HashSet<string> namespaces,
        HashSet<string> functions,
        HashSet<string> classes)
    {
        foreach ( ReferenceEntry entry in result.Extraction.References )
        {
            if ( entry.Kind == ReferenceKind.Definition )
            {
                continue;
            }

            if ( entry.Key.Kind == SymbolKind.Function )
            {
                string? namespaceName = entry.Key.Namespace;
                if ( namespaceName is not null )
                {
                    namespaces.Add(namespaceName);
                    functions.Add(FunctionKey(namespaceName, entry.Key.Name));
                }

                continue;
            }

            if ( entry.Key.Kind == SymbolKind.Class )
            {
                classes.Add(entry.Key.Name);
            }
        }
    }

    private static bool IsUsed(
        ScriptRecord record,
        HashSet<string> referencedNamespaces,
        HashSet<string> referencedFunctions,
        HashSet<string> referencedClasses)
    {
        foreach ( FunctionSymbol function in record.Functions )
        {
            // An autoexec runs on its own; importing the file IS the point.
            if ( function.IsAutoexec )
            {
                return true;
            }

            if ( referencedFunctions.Contains(FunctionKey(function.Namespace, function.KeyName)) )
            {
                return true;
            }
        }

        foreach ( ClassSymbol declared in record.Classes )
        {
            if ( referencedClasses.Contains(declared.KeyName) )
            {
                return true;
            }
        }

        // Namespace merging: this import may be what makes the namespace available even
        // though the called function is declared in another contributing file.
        foreach ( NamespaceSpan span in record.Namespaces )
        {
            if ( referencedNamespaces.Contains(span.KeyName) )
            {
                return true;
            }
        }

        return false;
    }

    private static string FunctionKey(string namespaceName, string name)
    {
        return namespaceName + "::" + name;
    }
}
