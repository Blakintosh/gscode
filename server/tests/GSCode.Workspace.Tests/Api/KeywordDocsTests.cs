using GSCode.Parser.Lexing;
using GSCode.Workspace.Api;
using Xunit;

namespace GSCode.Workspace.Tests.Api;

/// <summary>
/// Hover on a keyword needs two independent things to line up, in two different projects:
/// <see cref="TokenFacts.IsKeyword"/> must accept the token kind, and <see cref="KeywordDocs"/> must
/// hold an entry under the word. HoverHandler checks the first and then looks up the second, so a
/// keyword failing either one hovers as nothing at all — silently, with no build error and no
/// failing test anywhere else.
/// </summary>
public class KeywordDocsTests
{
    [Theory]
    [InlineData("vararg")]
    [InlineData("thisthread")]
    [InlineData("isdefined")]
    [InlineData("waittill")]
    [InlineData("size")]
    [InlineData("#using")]
    public void ADocumentedWordIsFound(string word)
    {
        Assert.False(string.IsNullOrWhiteSpace(KeywordDocs.Find(word)));
    }

    [Fact]
    public void TheParameterPackDocumentsHowItIsBound()
    {
        // The one thing a reader needs that the word itself does not say: it comes from `...`, and
        // it is an array. Without that the hover would restate the name.
        string doc = KeywordDocs.Find("vararg")!;

        Assert.Contains("...", doc, StringComparison.Ordinal);
        Assert.Contains("array", doc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheParameterPackAlsoPassesTheKeywordGateHoverChecksFirst()
    {
        // Documentation alone is not enough: HoverHandler returns before the lookup when the token
        // kind is not a keyword, which is exactly how `vararg` came to have no hover at all.
        Assert.True(Keywords.TryMatchKeyword("vararg", out TokenKind kind));
        Assert.True(TokenFacts.IsKeyword(kind));
    }

    [Fact]
    public void AnUndocumentedWordIsNull()
    {
        Assert.Null(KeywordDocs.Find("not_a_keyword_at_all"));
    }

    [Theory]
    [InlineData("assert")]
    [InlineData("assertmsg")]
    public void AKeywordTheEngineDocumentsIsAbsentHereOnPurpose(string word)
    {
        // These are keywords AND engine functions, and this file deliberately leaves them to the
        // builtin API. That was only safe once HoverHandler actually CONSULTED the API for a keyword
        // it has no entry for — before that the two halves pointed at each other and hovering
        // `assert` produced nothing. If a doc is ever added here it will simply win; the point of
        // this test is that the absence stays deliberate rather than becoming a second bug.
        Assert.Null(KeywordDocs.Find(word));
        Assert.True(Keywords.TryMatchKeyword(word, out TokenKind kind));
        Assert.True(TokenFacts.IsKeyword(kind));
    }

    [Fact]
    public void TheProfilerPairIsKeyedUnderOneSpellingOfTwo()
    {
        // prof_begin/prof_end are the Infinity Ward-line spelling and lex to the same token kinds as
        // profilestart/profilestop, but only the BO3 spelling is a key here. A lookup by TEXT
        // therefore missed the pair on the four games that actually write it that way, which is why
        // HoverHandler resolves the name through the token KIND instead.
        Assert.NotNull(KeywordDocs.Find("profilestart"));
        Assert.NotNull(KeywordDocs.Find("profilestop"));
        Assert.Null(KeywordDocs.Find("prof_begin"));
        Assert.Null(KeywordDocs.Find("prof_end"));

        Assert.True(Keywords.TryMatchKeyword("prof_begin", out TokenKind begin));
        Assert.True(Keywords.TryMatchKeyword("profilestart", out TokenKind start));
        Assert.Equal(start, begin);

        Assert.True(Keywords.TryMatchKeyword("prof_end", out TokenKind end));
        Assert.True(Keywords.TryMatchKeyword("profilestop", out TokenKind stop));
        Assert.Equal(stop, end);
    }
}
