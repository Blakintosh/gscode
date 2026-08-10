using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Extraction;

/// <summary>
/// Redeclaring a function in one file is an error, and the diagnostic carries a related
/// location pointing back at the first declaration.
/// </summary>
public class DuplicateFunctionTests
{
    private const string Path = @"c:\work\scripts\test.gsc";

    private static ParseResult Analyze(string source)
    {
        return ScriptAnalysis.Analyze(
            Path, ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());
    }

    private static ImmutableArray<Diagnostic> Duplicates(ParseResult result)
    {
        return result.AllDiagnostics
            .Where(diagnostic => diagnostic.Code == GscDiagnosticCode.DuplicateFunction)
            .ToImmutableArray();
    }

    [Fact]
    public void Redeclaration_ReportsErrorPointingAtTheFirstDefinition()
    {
        ParseResult result = Analyze("function alpha()\n{\n}\nfunction alpha()\n{\n}\n");

        Diagnostic diagnostic = Assert.Single(Duplicates(result));

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        // Reported on the SECOND declaration (line 3)...
        Assert.Equal(3, diagnostic.Range.Start.Line);

        // ...and points back at the first (line 0), in this same file.
        DiagnosticRelation relation = Assert.Single(diagnostic.RelatedInformation);
        Assert.Equal(0, relation.Range.Start.Line);
        Assert.Equal(Path, relation.FilePath);
    }

    [Fact]
    public void DifferingCase_StillCounts_BecauseIdentifiersAreCaseInsensitive()
    {
        ParseResult result = Analyze("function Alpha()\n{\n}\nfunction alpha()\n{\n}\n");

        Assert.Single(Duplicates(result));
    }

    [Fact]
    public void SameNameInDifferentNamespaces_IsFine()
    {
        ParseResult result = Analyze(
            "#namespace one;\nfunction alpha()\n{\n}\n#namespace two;\nfunction alpha()\n{\n}\n");

        Assert.Empty(Duplicates(result));
    }

    [Fact]
    public void ClassMethod_DoesNotCollideWithATopLevelFunction()
    {
        // The class scopes its methods, so this pair is legal.
        ParseResult result = Analyze("function alpha()\n{\n}\nclass Holder\n{\n    function alpha()\n    {\n    }\n}\n");

        Assert.Empty(Duplicates(result));
    }

    [Fact]
    public void ThreeDeclarations_ReportTwoDuplicates_BothPointingAtTheFirst()
    {
        ParseResult result = Analyze("function alpha()\n{\n}\nfunction alpha()\n{\n}\nfunction alpha()\n{\n}\n");

        ImmutableArray<Diagnostic> duplicates = Duplicates(result);

        Assert.Equal(2, duplicates.Length);
        Assert.All(duplicates, diagnostic => Assert.Equal(0, Assert.Single(diagnostic.RelatedInformation).Range.Start.Line));
    }
}
