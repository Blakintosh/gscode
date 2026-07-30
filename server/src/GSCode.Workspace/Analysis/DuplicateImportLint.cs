using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Parser;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// Reports the same file imported twice — <c>#using</c> on BO3, <c>#include</c> elsewhere.
///
/// Harmless at runtime, which is why it accumulates: nothing ever complains, so a merge or a
/// copied header block leaves two of them and the file grows a third next time.
///
/// The quick fix for this has existed since the code-action work
/// (<c>CodeActionHandler.FindRemovableDuplicates</c>) and nothing reported the problem, so it was
/// only reachable by putting the cursor on the offending line and going looking. A quick fix
/// nobody can find is a quick fix nobody has.
/// </summary>
public static class DuplicateImportLint
{
    public static ImmutableArray<Diagnostic> Analyze(ParseResult result)
    {
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        // Each directive keeps its own set: `#using x` and `#include x` are different mechanisms,
        // and a dialect only ever has one of them, so they can never collide in practice.
        HashSet<string> seenUsings = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seenIncludes = new(StringComparer.OrdinalIgnoreCase);

        foreach ( AstNode element in result.Tree.Root.Elements )
        {
            switch ( element )
            {
                case UsingNode usingNode:
                    Report(usingNode.Path, usingNode.PathRange, "#using", seenUsings, diagnostics);
                    continue;
                case IncludeNode includeNode:
                    Report(includeNode.Path, includeNode.PathRange, "#include", seenIncludes, diagnostics);
                    continue;
            }
        }

        return diagnostics.ToImmutable();
    }

    private static void Report(
        string path,
        GSCode.Core.Text.TextRange range,
        string directive,
        HashSet<string> seen,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        // Only the second and later occurrences are redundant; the first is the real import.
        if ( seen.Add(Normalize(path)) )
        {
            return;
        }

        Diagnostic duplicate = Diagnostic.Create(
            range, DiagnosticSeverity.Warning, GscDiagnosticCode.DuplicateImport, path, directive);

        // Tagged Unnecessary as well as reported: the whole line can go, and greying it out says
        // that more directly than the message does.
        diagnostics.Add(duplicate with { Tags = [DiagnosticTag.Unnecessary] });
    }

    /// <summary>
    /// Canonical form for comparison: separators unified and case folded, because
    /// <c>scripts/shared/util</c> and <c>scripts\shared\util</c> import the same file and the engine
    /// resolves either.
    /// </summary>
    private static string Normalize(string path)
    {
        return path.Replace('/', '\\').Trim();
    }
}
