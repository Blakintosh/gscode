using System.Collections.Generic;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Parser.Syntax.Ast;
using GSCode.Server.Handlers;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

public class CodeActionHandlerTests
{
    private static ParseResult Analyze(string source)
    {
        return ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());
    }

    private static TextRange WholeFile => TextRange.FromCoordinates(0, 0, 1000, 0);

    [Fact]
    public void FindsDuplicateUsing_ByPath()
    {
        string source = "#using scripts\\shared\\util;\n#using scripts\\shared\\util;\nfunction f(){}\n";

        List<UsingNode> duplicates = CodeActionHandler.FindRemovableDuplicates(Analyze(source), WholeFile);

        Assert.Single(duplicates);
        // The SECOND occurrence (line 1) is the redundant one.
        Assert.Equal(1, duplicates[0].Range.Start.Line);
    }

    [Fact]
    public void TreatsPathsCaseInsensitively()
    {
        string source = "#using scripts\\shared\\Util;\n#using scripts\\shared\\util;\nfunction f(){}\n";

        List<UsingNode> duplicates = CodeActionHandler.FindRemovableDuplicates(Analyze(source), WholeFile);

        Assert.Single(duplicates);
    }

    [Fact]
    public void NoDuplicates_WhenPathsDiffer()
    {
        string source = "#using scripts\\shared\\util;\n#using scripts\\shared\\array;\nfunction f(){}\n";

        List<UsingNode> duplicates = CodeActionHandler.FindRemovableDuplicates(Analyze(source), WholeFile);

        Assert.Empty(duplicates);
    }

    [Fact]
    public void OnlyOffersDuplicatesOverlappingTheSelection()
    {
        string source = "#using scripts\\shared\\util;\n#using scripts\\shared\\util;\nfunction f(){}\n";

        // A selection covering only line 0 (the first, non-redundant occurrence).
        TextRange lineZero = TextRange.FromCoordinates(0, 0, 0, 5);
        List<UsingNode> duplicates = CodeActionHandler.FindRemovableDuplicates(Analyze(source), lineZero);

        Assert.Empty(duplicates);
    }
}
