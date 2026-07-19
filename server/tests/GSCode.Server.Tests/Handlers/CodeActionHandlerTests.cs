using System.Collections.Generic;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Parser.Syntax.Ast;
using GSCode.Workspace.Database;
using GSCode.Workspace.Resolution;
using GSCode.Server.Handlers;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

public class CodeActionHandlerTests
{
    private static ParseResult Analyze(string source)
    {
        return AnalyzeAt(source, @"c:\ws\scripts\t.gsc");
    }

    private static ParseResult AnalyzeAt(string source, string path)
    {
        return ScriptAnalysis.Analyze(
            path, ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());
    }

    private static TextRange WholeFile => TextRange.FromCoordinates(0, 0, 1000, 0);

    private static ScriptDatabase DatabaseWithUtil()
    {
        ScriptDatabase database = new();
        ParseResult util = AnalyzeAt("#namespace util;\nfunction helper()\n{\n}\n", @"C:\bo3\share\raw\scripts\util.gsc");
        database.Commit(util, ResolutionContext.RawContext, false, "scripts\\util.gsc");
        return database;
    }

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

    [Fact]
    public void FindsMissingUsing_ForUnimportedQualifiedCall()
    {
        ScriptDatabase database = DatabaseWithUtil();
        string askingPath = @"C:\bo3\share\raw\scripts\main.gsc";
        ParseResult asking = AnalyzeAt("#namespace game;\nfunction run()\n{\n    util::helper();\n}\n", askingPath);

        List<string> missing = CodeActionHandler.FindMissingUsings(asking, database.Gsc, "raw", askingPath, WholeFile);

        Assert.Equal(new[] { "scripts\\util" }, missing);
    }

    [Fact]
    public void NoMissingUsing_WhenAlreadyImported()
    {
        ScriptDatabase database = DatabaseWithUtil();
        string askingPath = @"C:\bo3\share\raw\scripts\main.gsc";
        ParseResult asking = AnalyzeAt(
            "#using scripts\\util;\n#namespace game;\nfunction run()\n{\n    util::helper();\n}\n", askingPath);

        List<string> missing = CodeActionHandler.FindMissingUsings(asking, database.Gsc, "raw", askingPath, WholeFile);

        Assert.Empty(missing);
    }

    [Fact]
    public void NoMissingUsing_ForOwnNamespaceCall()
    {
        ScriptDatabase database = DatabaseWithUtil();
        string askingPath = @"C:\bo3\share\raw\scripts\util_more.gsc";
        ParseResult asking = AnalyzeAt("#namespace util;\nfunction run()\n{\n    util::helper();\n}\n", askingPath);

        List<string> missing = CodeActionHandler.FindMissingUsings(asking, database.Gsc, "raw", askingPath, WholeFile);

        Assert.Empty(missing);
    }
}
