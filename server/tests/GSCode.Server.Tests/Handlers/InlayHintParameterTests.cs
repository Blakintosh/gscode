using System.Collections.Frozen;
using System.Threading;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Resolution;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// The call-site parameter-name inlay family, driven through the handler the way the editor drives
/// it.
///
/// This family is ON by default and had no content test at all: the two that existed covered the
/// macro family (which is off) and the flow-pass cache's identity behaviour. Everything asserted
/// here is something a user sees — which argument a label sits on, whether a label appears twice,
/// and whether scrolling changes the answer.
/// </summary>
public class InlayHintParameterTests
{
    private const string MainPath = @"c:\bo3\share\raw\scripts\main.gsc";
    private const string UtilPath = @"c:\bo3\share\raw\scripts\util.gsc";

    /// <summary>
    /// One file exercising each callee form the family resolves, plus the two shapes that used to
    /// go wrong: a call whose arguments sit below its own first line, and a macro that names its
    /// argument twice.
    /// </summary>
    private const string MainSource =
        "#using scripts\\util;\n"                      // 0
        + "#namespace main;\n"                         // 1
        + "\n"                                         // 2
        + "#define TWICE( __a ) __a; __a;\n"           // 3
        + "\n"                                         // 4
        + "function apply( target, amount )\n"         // 5
        + "{\n"                                        // 6
        + "}\n"                                        // 7
        + "\n"                                         // 8
        + "function run( who )\n"                      // 9
        + "{\n"                                        // 10
        + "    apply( who, 3 );\n"                     // 11
        + "\n"                                         // 12
        + "    util::give_weapon(\n"                   // 13
        + "        who,\n"                             // 14
        + "        \"smg\" );\n"                       // 15
        + "\n"                                         // 16
        + "    TWICE( apply( who, 7 ) );\n"            // 17
        + "    apply( target, amount );\n"             // 18
        + "    custom_builtin( 5 );\n"                 // 19
        + "}\n";                                       // 20

    private const string UtilSource =
        "#namespace util;\n"
        + "function give_weapon( player, weapon )\n"   // 21
        + "{\n"                                        // 22
        + "}\n";                                       // 23

    /// <summary>Only the call-site family on, so nothing else can supply a hint under test.</summary>
    private static ServerSettings ParametersOnly()
    {
        return new ServerSettings
        {
            InlayInferredTypes = false,
            InlayParameterNames = true,
            InlayMacroParameterNames = false,
        };
    }

    /// <summary>
    /// One builtin, so the engine fallback can be asserted without depending on a shipped library
    /// whose parameter names belong to a game rather than to this test.
    /// </summary>
    private static BuiltinApiSet Builtins()
    {
        BuiltinFunction custom = new(
            "custom_builtin",
            "",
            [new BuiltinOverload(null, [new BuiltinParameter("duration", "", true, "int")], "", true)],
            "");

        BuiltinApi gsc = new(
            new Dictionary<string, BuiltinFunction> { ["custom_builtin"] = custom }
                .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));

        return new BuiltinApiSet(gsc, BuiltinApi.Empty);
    }

    private static InlayHintHandler BuildHandler(ServerSettings settings)
    {
        ScriptDatabase database = new();
        database.Commit(Analyze(UtilSource, UtilPath), ResolutionContext.RawContext, false, "scripts\\util.gsc");
        database.Commit(Analyze(MainSource, MainPath), ResolutionContext.RawContext, false, "scripts\\main.gsc");

        DocumentStore documents = new(static _ => NullInsertProvider.Instance, new NameTable());
        OpenDocument document = documents.Open(MainPath, MainSource, 1);
        documents.AnalyzeIfStale(document);

        NavigationSupport support = new(documents, database, new ResolverHolder(new PhysicalFileSystem()));

        return new InlayHintHandler(
            support, Builtins(), ObjectFields.Empty, settings, TextDocumentSelector.ForLanguage("gsc"));
    }

    private static ParseResult Analyze(string source, string path)
    {
        return ScriptAnalysis.Analyze(
            path, ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());
    }

    private static async Task<List<InlayHint>> HintsAsync(ServerSettings settings, LspRange? window = null)
    {
        InlayHintParams request = new()
        {
            TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(MainPath) },
            Range = window ?? new LspRange(0, 0, 30, 0),
        };

        InlayHintContainer? container = await BuildHandler(settings).Handle(request, CancellationToken.None);

        return [.. container ?? []];
    }

    /// <summary>The character a label sits at, so an assertion names the argument rather than a column.</summary>
    private static int ColumnOf(int line, string argument)
    {
        return MainSource.Split('\n')[line].IndexOf(argument, StringComparison.Ordinal);
    }

    private static InlayHint At(List<InlayHint> hints, int line, int character)
    {
        return Assert.Single(hints, hint => hint.Position.Line == line && hint.Position.Character == character);
    }

    [Fact]
    public async Task NoHintsWhenTheSettingIsOff()
    {
        ServerSettings settings = ParametersOnly();
        settings.InlayParameterNames = false;

        Assert.Empty(await HintsAsync(settings));
    }

    [Fact]
    public async Task ABareCallIsLabelledFromTheScriptFunctionItResolvesTo()
    {
        List<InlayHint> hints = await HintsAsync(ParametersOnly());

        InlayHint target = At(hints, 11, ColumnOf(11, "who"));
        Assert.Equal("target:", target.Label.String);
        Assert.Equal(InlayHintKind.Parameter, target.Kind);
        Assert.True(target.PaddingRight);

        Assert.Equal("amount:", At(hints, 11, ColumnOf(11, "3")).Label.String);
    }

    [Fact]
    public async Task AQualifiedCallIsLabelledFromTheImportedNamespace()
    {
        List<InlayHint> hints = await HintsAsync(ParametersOnly());

        Assert.Equal("player:", At(hints, 14, ColumnOf(14, "who")).Label.String);
        Assert.Equal("weapon:", At(hints, 15, ColumnOf(15, "\"smg\"")).Label.String);
    }

    [Fact]
    public async Task ABuiltinIsLabelledWhenNoScriptFunctionClaimsTheName()
    {
        List<InlayHint> hints = await HintsAsync(ParametersOnly());

        Assert.Equal("duration:", At(hints, 19, ColumnOf(19, "5")).Label.String);
    }

    [Fact]
    public async Task AnArgumentThatAlreadySpellsItsParameterIsNotLabelled()
    {
        // `apply( target, amount )` against `function apply( target, amount )` would render as
        // `apply( target: target, amount: amount )`, which says nothing the line does not already.
        // The fixture's other calls pass `who`, so they keep their labels — that is the half of
        // this rule worth pinning, since a suppression that fires too widely is silent.
        List<InlayHint> hints = await HintsAsync(ParametersOnly());

        Assert.DoesNotContain(hints, hint => hint.Position.Line == 18);
        Assert.Contains(hints, hint => hint.Position.Line == 11);
    }

    [Fact]
    public async Task ACallStartingAboveTheWindowStillLabelsItsVisibleArguments()
    {
        // `util::give_weapon(` is on line 13 and the window starts at 14, so testing the CALL's
        // start dropped both of its labels although both arguments are on screen. The editor
        // symptom was labels appearing and disappearing as a multi-line call crossed the top of
        // the viewport.
        List<InlayHint> hints = await HintsAsync(ParametersOnly(), new LspRange(14, 0, 16, 0));

        Assert.Equal(2, hints.Count);
        Assert.Equal("player:", At(hints, 14, ColumnOf(14, "who")).Label.String);
        Assert.Equal("weapon:", At(hints, 15, ColumnOf(15, "\"smg\"")).Label.String);
    }

    [Fact]
    public async Task AnArgumentBelowTheWindowIsNotLabelled()
    {
        // The other half of the rule: overlapping the window admits the CALL, not every label it
        // could produce. Line 15's argument stays out.
        List<InlayHint> hints = await HintsAsync(ParametersOnly(), new LspRange(13, 0, 15, 0));

        Assert.Equal("player:", Assert.Single(hints).Label.String);
    }

    [Fact]
    public async Task AMacroArgumentUsedTwiceIsLabelledOnce()
    {
        // `#define TWICE( __a ) __a; __a;` splices the argument's tokens twice, so the tree holds
        // two `apply( player, 7 )` calls at ONE range — and neither counts as expansion-born,
        // because the author did write that call. Without the dedupe both labelled the same
        // character.
        List<InlayHint> hints = await HintsAsync(ParametersOnly());

        Assert.Single(hints, hint => hint.Position.Line == 17 && hint.Label.String == "target:");
        Assert.Single(hints, hint => hint.Position.Line == 17 && hint.Label.String == "amount:");
    }
}
