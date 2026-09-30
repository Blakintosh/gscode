using GSCode.Core;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Workspace.Database;
using GSCode.Core.Diagnostics;
using GSCode.Server.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using LspDiagnostic = OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic;

namespace GSCode.Server.Tests.Handlers;

public class CodeActionHandlerTests
{
    private static TextRange WholeFile => TextRange.FromCoordinates(0, 0, 1000, 0);

    /// <summary>
    /// The import paths the add-#using fix would offer over the whole file. The sites carry the
    /// call each answers; these tests only ask which files would be imported.
    /// </summary>
    private static List<string> MissingUsingPaths(ParseResult asking, LanguageStore store, string askingPath)
    {
        List<string> paths = [];
        foreach ( CodeActionHandler.MissingUsing site in CodeActionHandler.FindMissingUsingSites(
            asking, store, "raw", askingPath, WholeFile) )
        {
            paths.Add(site.Path);
        }

        return paths;
    }

    /// <summary>
    /// A store holding scripts\util.gsc, which declares util::helper. The asking file is analysed on
    /// its own and never indexed. Disposed before it returns: the default game, so the queries
    /// that follow see the same Active either way.
    /// </summary>
    private static ScriptDatabase DatabaseWithUtil()
    {
        using TestWorkspace workspace = TestWorkspace.Build(
            [new TestFile(@"scripts\util.gsc", "#namespace util;\nfunction helper()\n{\n}\n")]);
        return workspace.Database;
    }

    [Fact]
    public void FindsDuplicateUsing_ByPath()
    {
        string source = "#using scripts\\shared\\util;\n#using scripts\\shared\\util;\nfunction f(){}\n";

        List<CodeActionHandler.RedundantImport> duplicates = CodeActionHandler.FindRemovableDuplicates(TestParse.Analyze(source), WholeFile);

        Assert.Single(duplicates);
        // The SECOND occurrence (line 1) is the redundant one.
        Assert.Equal(1, duplicates[0].Range.Start.Line);
    }

    [Fact]
    public void TreatsPathsCaseInsensitively()
    {
        string source = "#using scripts\\shared\\Util;\n#using scripts\\shared\\util;\nfunction f(){}\n";

        List<CodeActionHandler.RedundantImport> duplicates = CodeActionHandler.FindRemovableDuplicates(TestParse.Analyze(source), WholeFile);

        Assert.Single(duplicates);
    }

    [Fact]
    public void NoDuplicates_WhenPathsDiffer()
    {
        string source = "#using scripts\\shared\\util;\n#using scripts\\shared\\array;\nfunction f(){}\n";

        List<CodeActionHandler.RedundantImport> duplicates = CodeActionHandler.FindRemovableDuplicates(TestParse.Analyze(source), WholeFile);

        Assert.Empty(duplicates);
    }

    [Fact]
    public void OnlyOffersDuplicatesOverlappingTheSelection()
    {
        string source = "#using scripts\\shared\\util;\n#using scripts\\shared\\util;\nfunction f(){}\n";

        // A selection covering only line 0 (the first, non-redundant occurrence).
        TextRange lineZero = TextRange.FromCoordinates(0, 0, 0, 5);
        List<CodeActionHandler.RedundantImport> duplicates = CodeActionHandler.FindRemovableDuplicates(TestParse.Analyze(source), lineZero);

        Assert.Empty(duplicates);
    }

    [Fact]
    public void FindsDuplicateInclude_TheMergeDialectsImport()
    {
        // #include was skipped entirely, which left the four merge games reporting a duplicate
        // import (5018) with no fix behind it at all.
        //
        // Analysed as CoD4 on purpose: #include is gated by import style, so under BO3 it is not a
        // directive and no IncludeNode is produced at all. There is no dialect in which both forms
        // exist, which is why the two are tracked separately rather than compared with each other.
        string source = "#include maps\\_utility;\n#include maps\\_utility;\nmain(){}\n";
        ParseResult result = TestParse.Analyze(source, TestPaths.Raw(@"maps\t.gsc"), GameProfile.Cod4);

        CodeActionHandler.RedundantImport duplicate =
            Assert.Single(CodeActionHandler.FindRemovableDuplicates(result, WholeFile));

        Assert.Equal(1, duplicate.Range.Start.Line);
        Assert.Equal("#include", duplicate.Directive);
        Assert.Equal("maps\\_utility", duplicate.Path);
    }

    [Fact]
    public void FindsMissingUsing_ForUnimportedQualifiedCall()
    {
        ScriptDatabase database = DatabaseWithUtil();
        string askingPath = TestPaths.Raw(@"scripts\main.gsc");
        ParseResult asking = TestParse.Analyze("#namespace game;\nfunction run()\n{\n    util::helper();\n}\n", askingPath);

        List<string> missing = MissingUsingPaths(asking, database.Gsc, askingPath);

        Assert.Equal(new[] { "scripts\\util" }, missing);
    }

    [Fact]
    public void NoMissingUsing_WhenAlreadyImported()
    {
        ScriptDatabase database = DatabaseWithUtil();
        string askingPath = TestPaths.Raw(@"scripts\main.gsc");
        ParseResult asking = TestParse.Analyze(
            "#using scripts\\util;\n#namespace game;\nfunction run()\n{\n    util::helper();\n}\n", askingPath);

        List<string> missing = MissingUsingPaths(asking, database.Gsc, askingPath);

        Assert.Empty(missing);
    }

    [Fact]
    public void NoMissingUsing_ForOwnNamespaceCall()
    {
        ScriptDatabase database = DatabaseWithUtil();
        string askingPath = TestPaths.Raw(@"scripts\util_more.gsc");
        ParseResult asking = TestParse.Analyze("#namespace util;\nfunction run()\n{\n    util::helper();\n}\n", askingPath);

        List<string> missing = MissingUsingPaths(asking, database.Gsc, askingPath);

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
        ParseResult result = TestParse.Analyze(source);
        List<CommandOrCodeAction> actions = [];

        CodeActionParams request = new()
        {
            TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(TestPaths.Raw(@"scripts\t.gsc")) },
            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(0, 0, 1000, 0),
            Context = new CodeActionContext
            {
                Diagnostics = new Container<LspDiagnostic>(reported),
            },
        };

        CodeActionHandler.AddDiagnosticFixes(request, result, actions);

        // QuickFix only — every assertion below is about the diagnostic-driven fixes this method
        // returns. Organize Imports is NOT one of them any more (see AddOrganizeImportsAction's
        // own doc comment): it is built separately in Handle(), from a file-wide scan rather than
        // this method's request-scoped diagnostics, and is tested on its own below.
        List<CodeAction> fixes = [];
        foreach ( CommandOrCodeAction action in actions )
        {
            if ( action.IsCodeAction && action.CodeAction is not null && action.CodeAction.Kind == CodeActionKind.QuickFix )
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

        // Bound to EVERY diagnostic it clears, so it is reachable from any of those squiggles
        // rather than only from the general lightbulb.
        Assert.Equal(2, bulk.Diagnostics!.Count());

        // Auto Fix on one import must remove that import, not all of them.
        Assert.False(bulk.IsPreferred);
        Assert.All(
            fixes.Where(f => f.Title.StartsWith("Remove unused", StringComparison.Ordinal)),
            f => Assert.True(f.IsPreferred));
    }

    [Fact]
    public void UnusedInclude_IsRemovableToo()
    {
        // The merge dialects' import had no fix at all: the switch knew only about #using, so on
        // CoD4/WaW/MW2/BO1 an unused import was greyed out with nothing offered against it.
        string source = "#include maps\\a;\n#include maps\\b;\nmain(){}\n";

        List<CodeAction> fixes = FixesFor(
            source,
            Reported(GscDiagnosticCode.UnusedInclude, 0, 0, 16),
            Reported(GscDiagnosticCode.UnusedInclude, 1, 0, 16));

        Assert.Equal(3, fixes.Count);

        // The bulk title follows the DIRECTIVE, so a CoD4 user is not told about #using.
        CodeAction bulk = fixes.First(f => f.Title.StartsWith("Remove all", StringComparison.Ordinal));
        Assert.Equal("Remove all 2 unused #include directives", bulk.Title);
        Assert.Equal(2, bulk.Diagnostics!.Count());
    }

    [Fact]
    public void UnusedUsingsAndIncludes_DoNotShareABulkFix()
    {
        // No dialect has both forms, so this cannot arise in a real file — but one shared list
        // would have produced a single action whose title named the wrong directive.
        string source = "#using scripts\\a;\n#using scripts\\b;\n#include maps\\c;\n#include maps\\d;\nmain(){}\n";

        List<CodeAction> bulk = [.. FixesFor(
            source,
            Reported(GscDiagnosticCode.UnusedUsing, 0, 0, 17),
            Reported(GscDiagnosticCode.UnusedUsing, 1, 0, 17),
            Reported(GscDiagnosticCode.UnusedInclude, 2, 0, 16),
            Reported(GscDiagnosticCode.UnusedInclude, 3, 0, 16))
            .Where(f => f.Title.StartsWith("Remove all", StringComparison.Ordinal))];

        Assert.Equal(2, bulk.Count);
        Assert.Contains(bulk, f => f.Title.Contains("#using", StringComparison.Ordinal));
        Assert.Contains(bulk, f => f.Title.Contains("#include", StringComparison.Ordinal));
    }

    [Fact]
    public void OneUnusedUsing_DoesNotOfferABulkFix()
    {
        string source = "#using scripts\\a;\nfunction f(){}\n";

        List<CodeAction> fixes = FixesFor(source, Reported(GscDiagnosticCode.UnusedUsing, 0, 0, 17));

        Assert.DoesNotContain(fixes, f => f.Title.StartsWith("Remove all", StringComparison.Ordinal));
    }

    // --- Organize Imports (source.organizeImports) ---
    //
    // VS Code's "Source Action..." and "Organize Imports" commands ask for this kind specifically;
    // CodeActionHandler.CreateRegistrationOptions has to list it or neither menu ever reaches the
    // server at all. The action itself is built by AddOrganizeImportsAction in Handle(), from
    // AllUnusedImportDiagnostics' file-wide scan rather than the request's own scoped diagnostics
    // — the reported bug was Organize Imports on a multi-import file offering to remove only the
    // ONE unused import that happened to be on the line the menu was opened from. These tests pin
    // AddOrganizeImportsAction's own half directly: given a set of unused-import diagnostics —
    // wherever they came from — it combines every one of them into a single edit with no scoping
    // of its own. (AllUnusedImportDiagnostics' half — that the scan really is file-wide — needs a
    // real resolvable #using, which needs a workspace with a filesystem behind it; that is proven
    // by inspection instead: it takes no range/line parameter at all, unlike DiagnosticsForFixes.)

    private static DocumentUri TestUri => DocumentUri.FromFileSystemPath(TestPaths.Raw(@"scripts\t.gsc"));

    [Fact]
    public void UnusedImport_OffersAnOrganizeImportsAction_EvenJustOne()
    {
        // Unlike the QuickFix bulk action (OneUnusedUsing_DoesNotOfferABulkFix, above), Organize
        // Imports has no per-line neighbour to be redundant with — it is offered from one unused
        // import already.
        List<CommandOrCodeAction> actions = [];
        CodeActionHandler.AddOrganizeImportsAction(
            TestUri, [Reported(GscDiagnosticCode.UnusedUsing, 0, 0, 17)], actions);

        CodeAction organize = Assert.Single(actions, a => a.IsCodeAction).CodeAction!;
        Assert.Equal(CodeActionKind.SourceOrganizeImports, organize.Kind);
        Assert.Equal("", SingleEditOf(organize).NewText);
    }

    [Fact]
    public void OrganizeImportsAction_CombinesEveryUnusedImportIntoOneEdit()
    {
        // Two diagnostics on DIFFERENT lines — proving the action combines whatever set it is
        // given rather than assuming they share a line, which is exactly what a file-wide scan
        // (several imports, scattered through the file) hands it in practice.
        List<CommandOrCodeAction> actions = [];
        CodeActionHandler.AddOrganizeImportsAction(
            TestUri,
            [
                Reported(GscDiagnosticCode.UnusedUsing, 0, 0, 17),
                Reported(GscDiagnosticCode.UnusedUsing, 5, 0, 17),
            ],
            actions);

        CodeAction organize = Assert.Single(actions, a => a.IsCodeAction).CodeAction!;
        Assert.Equal(2, Assert.Single(organize.Edit!.Changes!).Value.Count());
        Assert.Equal(2, organize.Diagnostics!.Count());
    }

    [Fact]
    public void NoUnusedImports_OffersNoOrganizeImportsAction()
    {
        List<CommandOrCodeAction> actions = [];
        CodeActionHandler.AddOrganizeImportsAction(TestUri, [], actions);

        Assert.Empty(actions);
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

    // --- Unresolved calls (5013/5014) ---
    //
    // The diagnostic's range covers the NAME TOKEN only, never the `ns::` before it, which is what
    // lets qualifying be an insert at the range start and re-qualifying a replace of the scanned-back
    // qualifier.

    private static readonly string s_askingPath = TestPaths.Raw(@"scripts\main.gsc");

    private static List<CodeAction> CallFixes(string source, int line, int start, int end, ScriptDatabase? database = null)
    {
        ParseResult result = TestParse.Analyze(source, s_askingPath);

        CodeActionHandler.CallFixContext context = new(result, database?.Gsc, "raw", s_askingPath);

        return CodeActionHandler.UnresolvedCallFixes(
            DocumentUri.FromFileSystemPath(s_askingPath),
            context,
            Reported(GscDiagnosticCode.BuiltinFunctionNotFound, line, start, end));
    }

    [Fact]
    public void AnUnqualifiedUnresolvedCall_OffersToCreateTheFunction()
    {
        string source = "#namespace game;\nfunction run()\n{\n    missing();\n}\n";

        CodeAction fix = Assert.Single(CallFixes(source, 3, 4, 11), f => f.Title!.StartsWith("Create", StringComparison.Ordinal));

        Assert.Equal("Create function 'missing'", fix.Title);

        TextEdit edit = SingleEditOf(fix);

        // Appended at the end of the file, and opened the way BO3 declares a function.
        Assert.Contains("function missing()", edit.NewText, StringComparison.Ordinal);
        Assert.Equal(5, edit.Range.Start.Line);
        Assert.Equal(edit.Range.Start, edit.Range.End);
    }

    [Fact]
    public void AQualifiedUnresolvedCall_DoesNotOfferToCreateItHere()
    {
        // `other::missing()` says where it expects the function to be, and writing it into THIS file
        // would not put it there.
        string source = "#namespace game;\nfunction run()\n{\n    other::missing();\n}\n";

        Assert.DoesNotContain(
            CallFixes(source, 3, 11, 18), f => f.Title!.StartsWith("Create", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnqualifiedCallMatchingAnotherNamespace_OffersTheImportAndTheQualifier()
    {
        string source = "#namespace game;\nfunction run()\n{\n    helper();\n}\n";

        CodeAction fix = Assert.Single(
            CallFixes(source, 3, 4, 10, DatabaseWithUtil()),
            f => f.Title!.StartsWith("Add #using", StringComparison.Ordinal));

        Assert.Equal("Add #using scripts\\util and qualify with 'util::'", fix.Title);

        List<TextEdit> edits = [.. Assert.Single(fix.Edit!.Changes!).Value];
        Assert.Equal(2, edits.Count);

        // The import goes to the top; the qualifier is an INSERT at the name, so the call becomes
        // util::helper() without the name itself being rewritten.
        Assert.Contains(edits, e => e.NewText == "#using scripts\\util;\n" && e.Range.Start.Line == 0);

        TextEdit qualify = Assert.Single(edits, e => e.NewText == "util::");
        Assert.Equal(3, qualify.Range.Start.Line);
        Assert.Equal(4, qualify.Range.Start.Character);
        Assert.Equal(qualify.Range.Start, qualify.Range.End);
    }

    [Fact]
    public void AWronglyQualifiedCall_ReplacesTheQualifierRatherThanAddingOne()
    {
        // The namespace that WAS written is scanned back from the name and overwritten, so this ends
        // up as util::helper() rather than game::util::helper().
        string source = "#namespace game;\nfunction run()\n{\n    nope::helper();\n}\n";

        CodeAction fix = Assert.Single(
            CallFixes(source, 3, 10, 16, DatabaseWithUtil()),
            f => f.Title!.StartsWith("Add #using", StringComparison.Ordinal));

        TextEdit qualify = Assert.Single([.. Assert.Single(fix.Edit!.Changes!).Value], e => e.NewText == "util::");

        Assert.Equal(4, qualify.Range.Start.Character);
        Assert.Equal(10, qualify.Range.End.Character);
    }

    [Fact]
    public void AnAlreadyImportedNamespace_OffersOnlyTheQualifier()
    {
        string source = "#using scripts\\util;\n#namespace game;\nfunction run()\n{\n    helper();\n}\n";

        CodeAction fix = Assert.Single(
            CallFixes(source, 4, 4, 10, DatabaseWithUtil()),
            f => f.Title!.StartsWith("Qualify", StringComparison.Ordinal));

        Assert.Equal("Qualify with 'util::'", fix.Title);
        Assert.Equal("util::", SingleEditOf(fix).NewText);
    }

    [Fact]
    public void AnOwnNamespaceMatch_IsNotOffered()
    {
        // Already reachable unqualified, so an import would be noise and a qualifier a no-op.
        using TestWorkspace workspace = TestWorkspace.Build(
            [new TestFile(@"scripts\other.gsc", "#namespace game;\nfunction helper()\n{\n}\n")]);
        ScriptDatabase database = workspace.Database;

        string source = "#namespace game;\nfunction run()\n{\n    helper();\n}\n";

        Assert.DoesNotContain(
            CallFixes(source, 3, 4, 10, database), f => f.Title!.Contains("::", StringComparison.Ordinal));
    }

    [Fact]
    public void BothUnresolvedCallFixesAreAttachedToTheDiagnostic()
    {
        // An action with no diagnostic on it is a general lightbulb entry: never the fix FOR the
        // error, skipped by Auto Fix, and invisible to Fix All. That is the whole failure the 5000
        // import fix had.
        string source = "#namespace game;\nfunction run()\n{\n    helper();\n}\n";

        List<CodeAction> fixes = CallFixes(source, 3, 4, 10, DatabaseWithUtil());

        Assert.Equal(2, fixes.Count);
        Assert.All(fixes, f => Assert.Equal(
            (int)GscDiagnosticCode.BuiltinFunctionNotFound, Assert.Single(f.Diagnostics!).Code!.Value.Long));

        // The import is the actionable one and there is only one candidate, so Auto Fix may take it.
        // Creating an empty declaration makes the error vanish without the function doing anything,
        // so it is offered but never preferred.
        Assert.True(Assert.Single(fixes, f => f.Title!.StartsWith("Add #using", StringComparison.Ordinal)).IsPreferred);
        Assert.False(Assert.Single(fixes, f => f.Title!.StartsWith("Create", StringComparison.Ordinal)).IsPreferred);
    }

    [Fact]
    public void WithNoWorkspace_OnlyTheCreateFixIsOffered()
    {
        string source = "#namespace game;\nfunction run()\n{\n    helper();\n}\n";

        CodeAction fix = Assert.Single(CallFixes(source, 3, 4, 10));

        Assert.StartsWith("Create", fix.Title, StringComparison.Ordinal);
    }

    // --- Missing #include (5026) ---
    //
    // The merge dialects' counterpart. The function EXISTS here, so the only honest offer is the
    // import; a "create it here" fix would talk the user into a second copy of it.

    private static readonly GameProfile s_cod4 = GameProfile.ByName("cod4")!;

    private static readonly string s_cod4AskingPath = TestPaths.Raw(@"maps\mp\gametypes\_menus.gsc");

    /// <summary>A CoD4 workspace whose one file, common_scripts\utility.gsc, declares scriptPrintln.</summary>
    private static TestWorkspace WorkspaceWithCod4Utility()
    {
        return TestWorkspace.Build(
            [new TestFile(@"common_scripts\utility.gsc", "scriptPrintln( channel, msg )\n{\n}\n")], s_cod4);
    }

    /// <summary>
    /// The fixes for the 5026 reported over the <c>scriptPrintln</c> call in <paramref name="source"/>.
    /// The range is located from the text rather than passed in: every case here reports the same
    /// call, so hand-written coordinates would be three sets of magic numbers coupled to the sources
    /// they index into.
    /// </summary>
    private static List<CodeAction> IncludeFixes(string source, TestWorkspace? workspace = null)
    {
        ParseResult result = TestParse.Analyze(source, s_cod4AskingPath, s_cod4);

        const string called = "scriptPrintln";
        string[] lines = source.Split('\n');
        int line = Array.FindIndex(lines, text => text.Contains(called + "()", StringComparison.Ordinal));
        int start = lines[line].IndexOf(called, StringComparison.Ordinal);

        CodeActionHandler.CallFixContext context = new(
            result, workspace?.Database.Gsc, "raw", s_cod4AskingPath, s_cod4);

        return CodeActionHandler.MissingIncludeFixes(
            DocumentUri.FromFileSystemPath(s_cod4AskingPath),
            context,
            Reported(GscDiagnosticCode.FunctionNotIncluded, line, start, start + called.Length));
    }

    [Fact]
    public void AnUnincludedCall_OffersTheIncludeThatBringsItIntoScope()
    {
        string source = "#include maps\\mp\\_load;\ninit()\n{\n\tscriptPrintln();\n}\n";

        using TestWorkspace workspace = WorkspaceWithCod4Utility();
        CodeAction fix = Assert.Single(IncludeFixes(source, workspace));

        Assert.Equal(@"Add #include common_scripts\utility", fix.Title);

        // One candidate, so Auto Fix may take it.
        Assert.True(fix.IsPreferred);
        Assert.Equal(
            (int)GscDiagnosticCode.FunctionNotIncluded, Assert.Single(fix.Diagnostics!).Code!.Value.Long);

        TextEdit edit = SingleEditOf(fix);

        // Inserted after the last existing #include, not at line 0.
        Assert.Equal("#include common_scripts\\utility;\n", edit.NewText);
        Assert.Equal(1, edit.Range.Start.Line);
        Assert.Equal(edit.Range.Start, edit.Range.End);
    }

    [Fact]
    public void AnUnincludedCall_OffersNothingWhenTheFileIsAlreadyIncluded()
    {
        // A stale diagnostic against an edited buffer. Offering the import the file already has
        // would insert a duplicate and leave the error standing.
        string source = "#include common_scripts\\utility;\ninit()\n{\n\tscriptPrintln();\n}\n";

        using TestWorkspace workspace = WorkspaceWithCod4Utility();
        Assert.Empty(IncludeFixes(source, workspace));
    }

    [Fact]
    public void AnUnincludedCall_OffersNothingWithoutAWorkspace()
    {
        string source = "#include maps\\mp\\_load;\ninit()\n{\n\tscriptPrintln();\n}\n";

        Assert.Empty(IncludeFixes(source));
    }
}
