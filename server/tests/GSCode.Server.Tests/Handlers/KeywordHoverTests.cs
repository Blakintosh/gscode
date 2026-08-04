using System.Threading;
using GSCode.Core;
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

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Hover on a word that is BOTH a keyword and an engine function, driven through the handler.
///
/// Two paths can document a word and neither had these: KeywordDocs omits `assert`/`assertmsg` on
/// the grounds that the builtin API documents them, and the builtin API is only ever read from the
/// REFERENCE hover — which they never reach, because they lex as their own token kinds and
/// SymbolExtractor records an identifier callee only for TokenKind.Identifier. Each side assumed
/// the other had it, so hovering assert produced nothing at all.
///
/// Driven end to end rather than against KeywordDocs, because a unit test of either half is exactly
/// what missed this: both halves were behaving as designed.
/// </summary>
public class KeywordHoverTests
{
    private static async Task<Hover?> HoverAtAsync(string source, int line, int character, GameProfile? profile = null)
    {
        GameProfile game = profile ?? GameProfile.BlackOps3;
        string path = @"C:\bo3\share\raw\scripts\main.gsc";

        DocumentStore documents = new(static _ => NullInsertProvider.Instance, new NameTable());
        OpenDocument document = documents.Open(path, source, 1);
        documents.AnalyzeIfStale(document);

        ScriptDatabase database = new();
        ResolverHolder holder = new(new PhysicalFileSystem());
        NavigationSupport support = new(documents, database, holder);

        string api = Path.Combine(AppContext.BaseDirectory, "Api");
        HoverHandler handler = new(
            support,
            BuiltinApiSet.Load(api, game),
            ObjectFields.Load(api),
            TextDocumentSelector.ForLanguage("gsc"));

        HoverParams request = new()
        {
            TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(path) },
            Position = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Position(line, character),
        };

        return await handler.Handle(request, CancellationToken.None);
    }

    private static string TextOf(Hover hover)
    {
        return hover.Contents.MarkupContent!.Value;
    }

    [Theory]
    [InlineData("assert")]
    [InlineData("assertmsg")]
    public async Task AKeywordDocumentedOnlyByTheEngineStillHovers(string word)
    {
        string source = "#namespace game;\nfunction run()\n{\n    " + word + "( 1 );\n}\n";

        Hover? hover = await HoverAtAsync(source, 3, 5);

        Assert.NotNull(hover);
        Assert.Contains(word, TextOf(hover!), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AKeywordDocumentedByKeywordDocsStillWins()
    {
        // The fallback must not displace the hand-written docs, which say more than the API entry.
        string source = "#namespace game;\nfunction run()\n{\n    if ( isdefined( 1 ) )\n    {\n    }\n}\n";

        Hover? hover = await HoverAtAsync(source, 3, 10);

        Assert.NotNull(hover);
        Assert.Contains("undefined", TextOf(hover!), StringComparison.OrdinalIgnoreCase);
    }

    // The Infinity Ward spelling of the profiler pair (prof_begin/prof_end) cannot be driven from
    // here: DocumentStore analyses with GameProfile.Active, and selecting CoD4 would mutate
    // process-global state that every other test class in this assembly reads concurrently. The
    // mapping it depends on is pinned in KeywordDocsTests instead; this covers the path.
    [Fact]
    public async Task TheProfilerPairHovers()
    {
        string source = "#namespace game;\nfunction run()\n{\n    profilestart( \"x\" );\n}\n";

        Hover? hover = await HoverAtAsync(source, 3, 8);

        Assert.NotNull(hover);
        Assert.Contains("profil", TextOf(hover!), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnOrdinaryIdentifierIsNotClaimedByTheKeywordPath()
    {
        // The fallback sits behind the keyword gate, so it cannot start answering for names that
        // belong to the reference hover.
        string source = "#namespace game;\nfunction run()\n{\n    some_local = 1;\n}\n";

        Hover? hover = await HoverAtAsync(source, 3, 6);

        Assert.True(hover is null || !TextOf(hover).Contains("assert", StringComparison.OrdinalIgnoreCase));
    }
}
