using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Preprocessing;

/// <summary>
/// Excluded #if branches surface as Hint diagnostics tagged Unnecessary; that tag is the
/// mechanism the editor greys the range out with.
/// </summary>
public class InactiveBranchHintTests
{
    private static ParseResult Analyze(string source, string path = @"c:\work\scripts\test.gsc")
    {
        return ScriptAnalysis.Analyze(
            path,
            ScriptAnalysis.LanguageFromPath(path),
            SourceText.From(source),
            NullInsertProvider.Instance,
            new NameTable());
    }

    private static ImmutableArray<Diagnostic> InactiveHints(ParseResult result)
    {
        return result.AllDiagnostics
            .Where(diagnostic => diagnostic.Code == GscDiagnosticCode.InactiveConditionalBranch)
            .ToImmutableArray();
    }

    [Fact]
    public void ExcludedBranch_EmitsHintTaggedUnnecessary()
    {
        ParseResult result = Analyze("#if 0\ndropped = 1;\n#endif\nafter = 2;");

        Diagnostic hint = Assert.Single(InactiveHints(result));

        Assert.Equal(DiagnosticSeverity.Hint, hint.Severity);
        Assert.Equal(DiagnosticTag.Unnecessary, Assert.Single(hint.Tags));
        Assert.Equal(1, hint.Range.Start.Line);
    }

    [Fact]
    public void TakenBranch_EmitsNoHint()
    {
        ParseResult result = Analyze("#if 1\nkept = 1;\n#endif");

        Assert.Empty(InactiveHints(result));
    }

    [Fact]
    public void IfElifElse_HintsEveryBranchExceptTheWinner()
    {
        ParseResult result = Analyze("#if 0\na = 1;\n#elif 1\nb = 2;\n#elif 1\nc = 3;\n#else\nd = 4;\n#endif");

        // Three of the four branches lose; each greys out independently.
        Assert.Equal(3, InactiveHints(result).Length);
    }

    [Fact]
    public void OrdinaryFile_CarriesNoTagsOnNormalDiagnostics()
    {
        // Guards the default: only the grey-out rule sets Tags, so nothing else fades.
        ParseResult result = Analyze("function f()\n{\n    x = ;\n}\n");

        Assert.NotEmpty(result.AllDiagnostics);
        Assert.All(result.AllDiagnostics, diagnostic => Assert.Empty(diagnostic.Tags));
    }
}
