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
/// is imported purely for its side effects).
///
/// One unreadable <c>#using</c> used to suppress the whole pass, copied from
/// <see cref="NamespaceUsageLint"/> where it IS load-bearing. It is not load-bearing here, and the
/// question this rule asks is why: whether import Y is used depends on THIS FILE'S REFERENCES and
/// on Y'S OWN DECLARATIONS, and a file we cannot read is neither of those. Nor can it flip an
/// answer — if the unreadable file was the real provider of what this file calls, then Y provides
/// nothing referenced and saying so is right. So an unreadable directive is simply not judged (it
/// never entered <c>Usings</c>), and every other one still is: a workspace missing one script no
/// longer greys out nothing at all.
///
/// An unresolved <c>#insert</c> DOES suppress the pass, and that is the gate the old one was
/// standing in for without saying so. A header that did not expand takes its macros with it, so
/// <c>REGISTER_SYSTEM(...)</c> never becomes <c>system::register(...)</c> and the reference set is
/// short — which is exactly the shape that makes a live import look unused. The reference count is
/// this rule's INPUT, so a gate about macros belongs here in a way a gate about imports never did.
/// </summary>
public static class UnusedUsingLint
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

        if ( resolvedImports.Usings.Length == 0
            || ImportGate.AnyUnresolved(result, GscDiagnosticCode.InsertNotFound) )
        {
            return [];
        }

        // What this file actually reaches for: qualified namespaces, and function/class keys.
        HashSet<string> referencedNamespaces = new(StringComparer.Ordinal);
        HashSet<string> referencedFunctions = new(StringComparer.Ordinal);
        HashSet<string> referencedClasses = new(StringComparer.Ordinal);
        CollectReferences(result, referencedNamespaces, referencedFunctions, referencedClasses);

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach ( ImportedFile imported in resolvedImports.Usings )
        {
            if ( IsUsed(imported.Record, referencedNamespaces, referencedFunctions, referencedClasses) )
            {
                continue;
            }

            Diagnostic unused = Diagnostic.Create(
                imported.DirectiveRange, DiagnosticSeverity.Hint, GscDiagnosticCode.UnusedUsing, imported.RawPath);

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
            // A macro-expanded reference is deliberately NOT skipped: `REGISTER_SYSTEM(...)`
            // expands to `system::register(...)`, so a file using that macro genuinely needs its
            // `#using scripts\shared\system_shared`. Ignoring those uses told 471 stock files
            // their import was pointless. That holds even for the declaration-shaped ones, which is
            // why the flag is tested here rather than the kind alone.
            if ( entry.Kind == ReferenceKind.Definition && !entry.FromMacro )
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
        //
        // The DECLARED set, not the spans: an imported file whose own imports sit above its
        // #namespace line carries a leading span named after itself, and matching on that made the
        // import look used whenever anything referenced a namespace of that name.
        foreach ( string declared in record.DeclaredNamespaces )
        {
            if ( referencedNamespaces.Contains(declared) )
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
