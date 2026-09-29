using GSCode.Core;
using GSCode.Server.Handlers;
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
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(
            [new TestFile(@"scripts\main.gsc", source)], profile);
        workspace.Open(@"scripts\main.gsc");

        HoverHandler handler = new(
            workspace.Navigation, workspace.Builtins, workspace.ObjectFields, HandlerWorkspace.Selector);

        HoverParams request = new()
        {
            TextDocument = HandlerWorkspace.Identify(@"scripts\main.gsc"),
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

    // The Infinity Ward spelling of the profiler pair (prof_begin/prof_end) is pinned in
    // KeywordDocsTests; this covers the path. HoverAtAsync takes a profile, so a CoD4 case can be
    // driven from here now that the harness scopes GameProfile.Active and restores it.
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
