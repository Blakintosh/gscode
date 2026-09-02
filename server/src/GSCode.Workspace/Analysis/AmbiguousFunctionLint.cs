using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Syntax.Ast;
using GSCode.Workspace.Database;
using GSCode.Workspace.Resolution;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// Reports a function this file could reach by two different routes: the same
/// <c>namespace::name</c> declared in more than one of the files it links against. The call is
/// ambiguous, and which definition wins is not something the source says.
///
/// Scoped to the file's OWN imports, deliberately. Namespaces merge across files, so the same
/// name legitimately exists in several places that are never linked together — the stock scripts
/// contain 565 such pairs, almost all of them one game mode's copy against another's
/// (`scripts\mp\_ambient.csc` and `scripts\zm\_ambient.csc` declare the same 30-odd functions in
/// namespace `ambient`). A workspace-wide rule would report every one of them. Only the importing
/// file knows which definitions actually meet, which is why the question is asked here rather
/// than of the database as a whole.
///
/// One unreadable <c>#using</c> used to suppress the pass, on the stated grounds that a definition
/// we could not read "might be the one that makes a name ambiguous, or the one that makes it fine".
/// Only the first half was true. Ambiguity here is MONOTONIC: the claim is that two files this
/// script imports both declare the name, both of them are records in hand, and a third provider
/// nobody can read cannot reduce two to one. The reasoning that justifies a bail-out elsewhere was
/// copied to a rule whose answer it cannot change.
///
/// What an unreadable import DOES cost is the count in the message, which can only be understated —
/// it says how many of the files this script imports declare the name, and one it could not read is
/// not among them. A Warning that says two where the truth is three still points at the right
/// call.
///
/// The nine it reports on the stock scripts are real rather than tolerated noise:
/// <c>scripts\mp\_util.gsc:395</c> and <c>scripts\shared\util_shared.gsc:1663</c> both declare
/// <c>util::wait_endon</c> with the SAME parameter list, and <c>_dogs.gsc</c> imports both. Two
/// identical signatures reachable under one name is the ambiguity, whichever the linker happens
/// to pick — which is why this is a Warning rather than an Error: the code runs, but what it runs
/// is not something the source decides.
/// </summary>
public static class AmbiguousFunctionLint
{
    public static ImmutableArray<Diagnostic> Analyze(
        ParseResult result,
        LanguageStore store,
        ScriptLanguage language,
        PathResolver resolver,
        string askingPath,
        FileImports? imports = null)
    {
        string askingNormalized = PathUtil.NormalizeAbsolute(askingPath);

        // Resolved once per file by WorkspaceLints and shared with the other import lints; falling
        // back to resolving here keeps this callable on its own, which the tests rely on.
        FileImports resolvedImports = imports ?? FileImports.Resolve(result, store, language, resolver, askingPath);

        // namespace::name -> the files reachable from here that declare it.
        Dictionary<string, List<ScriptRecord>> providers = new(StringComparer.Ordinal);
        HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);

        foreach ( ImportedFile imported in resolvedImports.Usings )
        {
            // A file importing itself, or importing the same path twice, contributes once.
            if ( string.Equals(imported.Record.Path, askingNormalized, StringComparison.OrdinalIgnoreCase)
                || !seenPaths.Add(imported.Record.Path) )
            {
                continue;
            }

            Collect(imported.Record, providers);
        }

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        // Keyed on the symbol: one undecided call named twice by a macro body is one warning.
        // See MacroReports.
        HashSet<(TextRange Range, SymbolKey Key)>? reportedFromMacros = null;

        // Report at the CALL, not at the definitions: the definitions are each fine on their own,
        // and this file is where the ambiguity exists.
        foreach ( ReferenceEntry entry in result.Extraction.References )
        {
            // FromMacro is not skipped. The ambiguity is a property of what THIS file imports, and
            // invoking the macro is what brings the call into this file — a header body naming
            // `util::wait_endon` is as undecided here as writing it out, and for the same reason:
            // two of the files this one links against declare it.
            if ( !entry.IsFunctionCall )
            {
                continue;
            }

            if ( entry.Key.Namespace is null )
            {
                continue;
            }

            string key = entry.Key.Namespace + "::" + entry.Key.Name;
            if ( !providers.TryGetValue(key, out List<ScriptRecord>? declaring) || declaring.Count < 2 )
            {
                continue;
            }

            // Asked BEFORE the related-information array and the Diagnostic are built. Checking
            // afterwards did the work of reporting and then threw it away, which is the wrong order
            // for the one entry shape that can arrive twice.
            if ( !MacroReports.ShouldReport(entry, (entry.Range, entry.Key), ref reportedFromMacros) )
            {
                continue;
            }

            ImmutableArray<DiagnosticRelation> related =
            [
                .. declaring.Select(record => new DiagnosticRelation(
                    record.Path, FindNameRange(record, entry.Key), "Also defined here.")),
            ];

            Diagnostic ambiguous = Diagnostic.Create(
                entry.Range,
                DiagnosticSeverity.Warning,
                GscDiagnosticCode.AmbiguousFunction,
                entry.Key.Name,
                entry.Key.Namespace,
                declaring.Count);

            diagnostics.Add(ambiguous with { RelatedInformation = related });
        }

        return diagnostics.ToImmutable();
    }

    private static void Collect(ScriptRecord record, Dictionary<string, List<ScriptRecord>> providers)
    {
        foreach ( FunctionSymbol function in record.Functions )
        {
            // Inserted declarations belong to the header that holds them, not to this record.
            if ( function.SourceFile.Length > 0 || function.Namespace.Length == 0 )
            {
                continue;
            }

            string key = function.Namespace + "::" + function.KeyName;
            if ( !providers.TryGetValue(key, out List<ScriptRecord>? declaring) )
            {
                declaring = [];
                providers[key] = declaring;
            }

            if ( !declaring.Contains(record) )
            {
                declaring.Add(record);
            }
        }
    }

    private static TextRange FindNameRange(ScriptRecord record, SymbolKey key)
    {
        foreach ( FunctionSymbol function in record.Functions )
        {
            if ( function.KeyName == key.Name && function.Namespace == key.Namespace )
            {
                return function.NameRange;
            }
        }

        return TextRange.Empty;
    }
}
