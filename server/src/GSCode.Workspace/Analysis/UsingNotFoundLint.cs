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
/// </summary>
public static class UsingNotFoundLint
{
    public static ImmutableArray<Diagnostic> Analyze(
        ParseResult result,
        LanguageStore store,
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

            string? resolved = resolver.Resolve(context, usingNode.Path + extension);
            if ( resolved is not null && store.TryGet(PathUtil.NormalizeAbsolute(resolved), out _) )
            {
                continue;
            }

            // Resolving is not enough on its own: a path can resolve against a root and still not
            // be indexed, which means it is not a script this workspace knows about. Both cases
            // fail the same way at runtime, so both are reported the same way.
            diagnostics.Add(Diagnostic.Create(
                usingNode.PathRange,
                DiagnosticSeverity.Error,
                GscDiagnosticCode.UsingNotFound,
                usingNode.Path));
        }

        return diagnostics.ToImmutable();
    }
}
