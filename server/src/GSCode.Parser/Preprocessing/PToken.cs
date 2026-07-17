using GSCode.Core.Text;
using GSCode.Parser.Lexing;

namespace GSCode.Parser.Preprocessing;

/// <summary>
/// Where a preprocessed token really came from. All three fields default to "the root
/// file, as written" — the common case costs nothing.
/// </summary>
/// <param name="SourceFile">File containing the token's true location; null = the root file itself.</param>
/// <param name="RootSite">Range in the ROOT file to anchor diagnostics to (the #insert directive
/// or macro invocation); null = the token's own range is already in the root file.</param>
/// <param name="DefinitionSite">Range of the #define name that produced this token, when macro-expanded.</param>
public readonly record struct Provenance(string? SourceFile, TextRange? RootSite, TextRange? DefinitionSite)
{
    /// <summary>Provenance of a token sitting in the root file exactly where it was written.</summary>
    public static Provenance Root { get; } = new(null, null, null);
}

/// <summary>
/// One token of the parse stream: kind, materialized (interned) text, its range in its
/// true source file, and provenance. Trivia never reaches this stream — the parser
/// consumes PTokens directly; the formatter reads the raw lexer stream instead.
/// </summary>
public readonly record struct PToken(TokenKind Kind, string Text, TextRange Range, Provenance Provenance)
{
    /// <summary>The range in the root file to report diagnostics at for this token.</summary>
    public TextRange RootRange
    {
        get { return Provenance.RootSite ?? Range; }
    }
}
