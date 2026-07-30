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
}
