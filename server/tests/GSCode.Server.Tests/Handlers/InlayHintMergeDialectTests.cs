using System.Threading;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Server.Tests.Corpus;
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
/// Parameter-name hints on a MERGE dialect — CoD4, and with it WaW, MW2 and BO1.
///
/// These games have no <c>#namespace</c>, so the extractor defaults every function's namespace to
/// its file name stem, and <c>#include</c> is what brings another file's functions into scope. A
/// lookup by declared (stem) namespace therefore answers only for calls to the asking file's OWN
/// functions, and misses every cross-file call — which on CoD4 is most of them:
/// <c>maps\_utility::createOneshotEffect</c> alone is written 3,147 times in the shipped scripts.
///
/// Signature help already makes this split (SignatureEngine, on <c>ResolvesByNamespace</c>); these
/// pin that inlay hints make it too.
/// </summary>
[Collection(GameProfileCollection.Name)]
public class InlayHintMergeDialectTests
{
    private const string MainPath = @"c:\cod4\raw\maps\mymap.gsc";
    private const string UtilPath = @"c:\cod4\raw\maps\_utility.gsc";

    private const string MainSource =
        "#include maps\\_utility;\n"                           // 0
        + "\n"                                                 // 1
        + "main()\n"                                           // 2
        + "{\n"                                                // 3
        + "    set_ambient( \"amb\", 3 );\n"                   // 4
        + "    maps\\_utility::set_ambient( \"amb\", 4 );\n"   // 5
        + "    local_helper( 7 );\n"                           // 6
        + "}\n"                                                // 7
        + "\n"                                                 // 8
        + "local_helper( amount )\n"                           // 9
        + "{\n"                                                // 10
        + "}\n";                                               // 11

    private const string UtilSource =
        "set_ambient( ambient, fadetime )\n"
        + "{\n"
        + "}\n";

    private static ServerSettings ParametersOnly()
    {
        return new ServerSettings
        {
            InlayInferredTypes = false,
            InlayParameterNames = true,
            InlayMacroParameterNames = false,
        };
    }

    private static async Task<List<InlayHint>> HintsAsync()
    {
        GameProfile previous = GameProfile.Active;
        Assert.True(GameProfile.Select("cod4"));

        try
        {
            ScriptDatabase database = new();
            database.Commit(Analyze(UtilSource, UtilPath), ResolutionContext.RawContext, false, "maps\\_utility.gsc");
            database.Commit(Analyze(MainSource, MainPath), ResolutionContext.RawContext, false, "maps\\mymap.gsc");

            DocumentStore documents = new(static _ => NullInsertProvider.Instance, new NameTable());
            OpenDocument document = documents.Open(MainPath, MainSource, 1);
            documents.AnalyzeIfStale(document);

            InlayHintHandler handler = new(
                new NavigationSupport(documents, database, new ResolverHolder(new PhysicalFileSystem())),
                new BuiltinApiSet(BuiltinApi.Empty, BuiltinApi.Empty),
                ObjectFields.Empty,
                ParametersOnly(),
                TextDocumentSelector.ForLanguage("gsc"));

            InlayHintParams request = new()
            {
                TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(MainPath) },
                Range = new LspRange(0, 0, 20, 0),
            };

            InlayHintContainer? container = await handler.Handle(request, CancellationToken.None);

            return [.. container ?? []];
        }
        finally
        {
            Assert.True(GameProfile.Select(previous.ShortName));
        }
    }

    private static ParseResult Analyze(string source, string path)
    {
        return ScriptAnalysis.Analyze(
            path, ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());
    }

    private static string LabelAt(List<InlayHint> hints, int line, string argument)
    {
        int character = MainSource.Split('\n')[line].IndexOf(argument, StringComparison.Ordinal);

        return Assert.Single(hints, hint => hint.Position.Line == line && hint.Position.Character == character)
            .Label.String ?? "";
    }

    [Fact]
    public async Task ACallToTheFilesOwnFunctionIsLabelled()
    {
        // The stem-namespace lookup already answers this one, which is why the gap below was not
        // obvious: hints do appear on a merge dialect, just only for same-file calls.
        Assert.Equal("amount:", LabelAt(await HintsAsync(), 6, "7"));
    }

    [Fact]
    public async Task ABareCallReachedThroughAnIncludeIsLabelled()
    {
        List<InlayHint> hints = await HintsAsync();

        Assert.Equal("ambient:", LabelAt(hints, 4, "\"amb\""));
        Assert.Equal("fadetime:", LabelAt(hints, 4, "3"));
    }

    [Fact]
    public async Task APathQualifiedCallIsLabelled()
    {
        List<InlayHint> hints = await HintsAsync();

        Assert.Equal("ambient:", LabelAt(hints, 5, "\"amb\""));
        Assert.Equal("fadetime:", LabelAt(hints, 5, "4"));
    }
}
