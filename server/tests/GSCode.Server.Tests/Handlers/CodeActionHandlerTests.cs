using System.Collections.Generic;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Parser.Syntax.Ast;
using GSCode.Workspace.Database;
using GSCode.Workspace.Resolution;
using GSCode.Core.Diagnostics;
using GSCode.Server.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using LspDiagnostic = OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic;

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

    // --- Diagnostic-driven fixes (ported from the v1 handler) ---

    private static LspDiagnostic Reported(GscDiagnosticCode code, int line, int startCharacter, int endCharacter)
    {
        return new LspDiagnostic
        {
            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(line, startCharacter, line, endCharacter),
            Code = new DiagnosticCode((int)code),
            Message = "reported",
        };
    }

    private static List<CodeAction> FixesFor(string source, params LspDiagnostic[] reported)
    {
        ParseResult result = Analyze(source);
        List<CommandOrCodeAction> actions = [];

        CodeActionParams request = new()
        {
            TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(@"c:\ws\scripts\t.gsc") },
            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(0, 0, 1000, 0),
            Context = new CodeActionContext
            {
                Diagnostics = new Container<LspDiagnostic>(reported),
            },
        };

        CodeActionHandler.AddDiagnosticFixes(request, result, actions);

        List<CodeAction> fixes = [];
        foreach ( CommandOrCodeAction action in actions )
        {
            if ( action.IsCodeAction && action.CodeAction is not null )
            {
                fixes.Add(action.CodeAction);
            }
        }

        return fixes;
    }

    private static TextEdit SingleEditOf(CodeAction action)
    {
        return Assert.Single(Assert.Single(action.Edit!.Changes!).Value);
    }

    [Fact]
    public void UnusedUsing_OffersToRemoveTheWholeLine()
    {
        string source = "#using scripts\\shared\\util;\nfunction f(){}\n";

        CodeAction fix = Assert.Single(FixesFor(source, Reported(GscDiagnosticCode.UnusedUsing, 0, 0, 27)));

        Assert.Contains("Remove unused", fix.Title);
        TextEdit edit = SingleEditOf(fix);
        Assert.Equal("", edit.NewText);
        // The whole line goes, including its newline, so no blank line is left behind.
        Assert.Equal(0, edit.Range.Start.Line);
        Assert.Equal(1, edit.Range.End.Line);
        Assert.Equal(0, edit.Range.End.Character);
    }

    [Fact]
    public void SeveralUnusedUsings_AlsoOfferOneBulkFix()
    {
        string source = "#using scripts\\a;\n#using scripts\\b;\nfunction f(){}\n";

        List<CodeAction> fixes = FixesFor(
            source,
            Reported(GscDiagnosticCode.UnusedUsing, 0, 0, 17),
            Reported(GscDiagnosticCode.UnusedUsing, 1, 0, 17));

        // Two individual removals plus the bulk one.
        Assert.Equal(3, fixes.Count);
        CodeAction bulk = fixes.First(f => f.Title.StartsWith("Remove all", StringComparison.Ordinal));
        Assert.Equal(2, Assert.Single(bulk.Edit!.Changes!).Value.Count());
    }

    [Fact]
    public void OneUnusedUsing_DoesNotOfferABulkFix()
    {
        string source = "#using scripts\\a;\nfunction f(){}\n";

        List<CodeAction> fixes = FixesFor(source, Reported(GscDiagnosticCode.UnusedUsing, 0, 0, 17));

        Assert.DoesNotContain(fixes, f => f.Title.StartsWith("Remove all", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1", "true")]
    [InlineData("0", "false")]
    public void PreferBooleanLiteral_ReplacesTheLiteralItActuallyFinds(string literal, string expected)
    {
        // The replacement is read from the source, not parsed out of the diagnostic message.
        string source = "function f()\n{\n    AllowAttack( " + literal + " );\n}\n";
        int character = source.Split('\n')[2].IndexOf(literal, StringComparison.Ordinal);

        CodeAction fix = Assert.Single(
            FixesFor(source, Reported(GscDiagnosticCode.PreferBooleanLiteral, 2, character, character + 1)));

        TextEdit edit = SingleEditOf(fix);
        Assert.Equal(expected, edit.NewText);
        Assert.Contains(expected, fix.Title);
    }

    [Fact]
    public void PreferBooleanLiteral_OffersNothingWhenTheRangeIsNotZeroOrOne()
    {
        // A stale diagnostic pointing at edited text must not produce a nonsense edit.
        string source = "function f()\n{\n    AllowAttack( 7 );\n}\n";
        int character = source.Split('\n')[2].IndexOf('7');

        Assert.Empty(FixesFor(source, Reported(GscDiagnosticCode.PreferBooleanLiteral, 2, character, character + 1)));
    }

    [Fact]
    public void UsingAfterDeclaration_MovesItAboveTheFirstDeclaration()
    {
        string source = "#using scripts\\a;\nfunction f(){}\n#using scripts\\late;\n";

        CodeAction fix = Assert.Single(FixesFor(source, Reported(GscDiagnosticCode.UsingAfterDeclaration, 2, 0, 20)));

        Assert.Contains("Move", fix.Title);
        List<TextEdit> edits = [.. Assert.Single(fix.Edit!.Changes!).Value];

        // One deletion of the offending line, one insertion higher up.
        Assert.Equal(2, edits.Count);
        Assert.Contains(edits, e => e.NewText.Length == 0 && e.Range.Start.Line == 2);
        Assert.Contains(edits, e => e.NewText.Contains("#using", StringComparison.Ordinal) && e.Range.Start.Line < 2);
    }

    [Fact]
    public void UnrelatedDiagnostics_ProduceNoFixes()
    {
        string source = "function f(){}\n";

        Assert.Empty(FixesFor(source, Reported(GscDiagnosticCode.ExpectedToken, 0, 0, 5)));
    }
}
