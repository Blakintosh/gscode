using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Parser.Syntax.Ast;
using GSCode.Workspace.Resolution;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// Reports a <c>#using</c> whose target does not exist.
///
/// <c>#insert</c> has had this since the beginning, because the preprocessor must actually READ
/// the file and notices when it cannot. A <c>#using</c> is resolved lazily and was never checked
/// at all — so a typo produced no diagnostic whatsoever while failing to link at runtime.
///
/// It also compounds: <see cref="NamespaceUsageLint"/> and <see cref="UnusedUsingLint"/> both
/// abandon their pass when an import will not resolve, on the sound reasoning that a file they
/// cannot read might supply the namespace they were about to complain about. With nothing
/// reporting the bad import itself, one typo silently switched off namespace checking for the
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
        string askingPath)
    {
        ResolutionContext context = resolver.GetContext(askingPath);
        string extension = language == ScriptLanguage.Csc ? GameProfile.Active.ClientScriptExtension : GameProfile.Active.ServerScriptExtension;

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        foreach ( AstNode element in result.Tree.Root.Elements )
        {
            if ( element is not UsingNode usingNode )
            {
                continue;
            }

            // Resolve probes the roots and checks the file EXISTS on disk, which is exactly what
            // decides whether the script links at runtime. That is the whole test — deliberately
            // NOT "is it in the workspace index": a valid target the initial index has not reached
            // yet (or an oversized file it skipped) exists all the same, and gating on the index
            // turned a startup race into a false gscode-5009 on correct scripts.
            if ( resolver.Resolve(context, usingNode.Path + extension) is not null )
            {
                continue;
            }

            diagnostics.Add(Diagnostic.Create(
                usingNode.PathRange,
                DiagnosticSeverity.Error,
                GscDiagnosticCode.UsingNotFound,
                usingNode.Path));
        }

        return diagnostics.ToImmutable();
    }
}
