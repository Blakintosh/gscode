using GSCode.Core.Text;
using GSCode.Parser.Lexing;

namespace GSCode.Parser.Preprocessing;

/// <summary>
/// Where a preprocessed token really came from. All three fields default to "the root
/// file, as written" — the common case costs nothing.
///
/// A CLASS, held by reference, and that is a memory decision rather than a modelling one.
/// Provenance describes an expansion SITE — an #insert directive, a macro invocation — of which a
/// file has a handful, but it is carried by every token in the parse stream. Inline it was 48
/// bytes (two nullable TextRanges at 20 each, plus the string) inside an 80-byte PToken, so more
/// than half of every token described where it came from, and for the overwhelming majority of
/// tokens the answer was three nulls. By reference it is one pointer, and every token that came
/// from the root file shares <see cref="Root"/>.
///
/// A record, so equality stays structural and comparisons that used to compare values still do.
/// </summary>
/// <param name="SourceFile">File containing the token's true location; null = the root file itself.</param>
/// <param name="RootSite">Range in the ROOT file to anchor diagnostics to (the #insert directive
/// or macro invocation); null = the token's own range is already in the root file.</param>
/// <param name="DefinitionSite">Range of the #define name that produced this token, when macro-expanded.</param>
public sealed record Provenance(string? SourceFile, TextRange? RootSite, TextRange? DefinitionSite)
{
    /// <summary>
    /// Provenance of a token sitting in the root file exactly where it was written — shared by
    /// every such token rather than copied into each.
    /// </summary>
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
        // Null-conditional for the default struct only: every constructed PToken carries a
        // provenance, but `default(PToken)` cannot be stopped from existing.
        get { return Provenance?.RootSite ?? Range; }
    }
}
