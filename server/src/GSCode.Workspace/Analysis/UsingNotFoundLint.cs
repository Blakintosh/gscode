using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Syntax.Ast;
using GSCode.Workspace.Resolution;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// Reports an import — <c>#using</c> or <c>#include</c> — whose target does not exist. Named after
/// the code it raises (<see cref="GscDiagnosticCode.UsingNotFound"/>), which covers both because no
/// dialect has both spellings and "Cannot find script '{0}'." is the same sentence either way.
///
/// <c>#insert</c> has had this since the beginning, because the preprocessor must actually READ
/// the file and notices when it cannot. The lazily-resolved imports were never checked at all —
/// so a typo produced no diagnostic whatsoever while failing to link at runtime.
///
/// It also compounds, on both sides. <see cref="NamespaceUsageLint"/> and
/// <see cref="UnusedUsingLint"/> abandon their pass when a <c>#using</c> will not resolve, and
/// <see cref="IncludeUsageLint"/> and <see cref="UnusedIncludeLint"/> do the same for an
/// <c>#include</c>, on the sound reasoning that a file they cannot read might supply the name they
/// were about to complain about. With nothing reporting the bad import itself, one typo silently
/// switched off namespace checking — or, on a merge dialect, the Error-severity 5026 — for the
/// whole file and left no trace of why.
///
/// Error severity: the script does not load. That is not a matter of taste.
///
/// "Not found" means the path does not resolve to a file on disk — the same thing that decides
/// whether the game links it. It deliberately does not consult the workspace index: a target the
/// initial index has not reached yet still exists, and testing the index would report a false
/// error on correct scripts during startup.
/// </summary>
public static class UsingNotFoundLint
{
    public static ImmutableArray<Diagnostic> Analyze(
        ParseResult result,
        ScriptLanguage language,
        PathResolver resolver,
        string askingPath,
        GameProfile? profile = null)
    {
        ResolutionContext context = resolver.GetContext(askingPath);
        string extension = (profile ?? GameProfile.Active).ExtensionFor(language);

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        foreach ( AstNode element in result.Tree.Root.Elements )
        {
            // Both import spellings, since the defect is the same one and so is the fix. No dialect
            // has both — the lexer gates each directive on ImportStyle — so no file can produce the
            // two kinds at once and the rule needs no profile gate of its own.
            string path;
            TextRange pathRange;

            switch ( element )
            {
                case UsingNode usingNode:
                    path = usingNode.Path;
                    pathRange = usingNode.PathRange;
                    break;
                case IncludeNode includeNode:
                    path = includeNode.Path;
                    pathRange = includeNode.PathRange;
                    break;
                default:
                    continue;
            }

            // Resolve probes the roots and checks the file EXISTS on disk, which is exactly what
            // decides whether the script links at runtime. That is the whole test — deliberately
            // NOT "is it in the workspace index": a valid target the initial index has not reached
            // yet (or an oversized file it skipped) exists all the same, and gating on the index
            // turned a startup race into a false gscode-5009 on correct scripts.
            if ( resolver.Resolve(context, path + extension) is not null )
            {
                continue;
            }

            diagnostics.Add(Diagnostic.Create(
                pathRange,
                DiagnosticSeverity.Error,
                GscDiagnosticCode.UsingNotFound,
                path));
        }

        return diagnostics.ToImmutable();
    }
}
