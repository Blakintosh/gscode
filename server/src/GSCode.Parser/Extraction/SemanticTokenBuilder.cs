using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Parser.Lexing;

namespace GSCode.Parser.Extraction;

/// <summary>
/// Produces semantic highlight tokens for a file, and IDENTIFIERS are the whole of it: each is
/// classified from the extracted reference list as a function, class, macro or field. Every token
/// that can be classified is a single-line name, which is what the LSP encoding requires.
/// </summary>
public static class SemanticTokenBuilder
{
    /// <summary>Builds the ordered, non-overlapping semantic tokens for a parsed file.</summary>
    public static ImmutableArray<SemanticToken> Build(ParseResult result)
    {
        // Classify identifier spans by their start position from the reference list.
        Dictionary<(int Line, int Char), SemanticTokenType> classified = new();
        foreach ( ReferenceEntry entry in result.Extraction.References )
        {
            SemanticTokenType? type = ClassifyReference(entry.Key.Kind);
            if ( type is not null )
            {
                // A definition/use of a name: key by its start; last write wins (harmless).
                classified[(entry.Range.Start.Line, entry.Range.Start.Character)] = type.Value;
            }
        }

        List<SemanticToken> tokens = [];

        foreach ( Token token in result.Lexed.Tokens )
        {
            SemanticTokenType? type = ClassifyToken(token, classified);
            if ( type is null )
            {
                continue;
            }

            tokens.Add(new SemanticToken(token.Range.Start.Line, token.Range.Start.Character, token.Length, type.Value));
        }

        tokens.Sort(static (left, right) =>
        {
            int lineCompare = left.Line.CompareTo(right.Line);
            return lineCompare != 0 ? lineCompare : left.StartChar.CompareTo(right.StartChar);
        });

        return [.. tokens];
    }

    private static SemanticTokenType? ClassifyReference(SymbolKind kind)
    {
        switch ( kind )
        {
            case SymbolKind.Function:
                return SemanticTokenType.Function;
            case SymbolKind.Class:
                return SemanticTokenType.Type;
            case SymbolKind.Macro:
                return SemanticTokenType.Macro;
            case SymbolKind.Field:
            case SymbolKind.Member:
                return SemanticTokenType.Property;
            default:
                return null;
        }
    }

    /// <summary>
    /// What an IDENTIFIER means — function, class, macro or field — is a question about the
    /// workspace rather than the characters, and the one thing a grammar cannot answer; that is the
    /// whole reason semantic tokens exist. Everything lexical (comments, strings, numbers, keywords)
    /// is left to the TextMate grammar. A semantic token can only repaint a grammar scope, never
    /// remove one, so for a category the grammar already knows it either agrees — and only flickers
    /// from one shade to another when the server's tokens arrive — or disagrees and loses anyway.
    /// Over a comment it is worse: painting a whole <c>/@ … @/</c> block as one Comment flattened the
    /// descriptors, argument names and types the grammar colours inside it.
    ///
    /// Parameters and locals are not classified here either: <see cref="SymbolKind"/> has no member
    /// for them. <c>LocalReferences.SemanticTokens</c> in the workspace fills those legend slots from
    /// the AST, merged in by the handler.
    /// </summary>
    private static SemanticTokenType? ClassifyToken(Token token, Dictionary<(int, int), SemanticTokenType> classified)
    {
        bool isKeyword = TokenFacts.IsKeyword(token.Kind);
        if ( token.Kind != TokenKind.Identifier && !isKeyword )
        {
            return null;
        }

        if ( !classified.TryGetValue((token.Range.Start.Line, token.Range.Start.Character), out SemanticTokenType type) )
        {
            return null;
        }

        // A keyword is painted only when it is a keyword-shaped MACRO name. Keywords match
        // case-insensitively, so `DEFAULT` lexes as TokenKind.Default even though the preprocessor
        // accepts it as a macro (BO3's own DEFAULT() in shared.gsh) and records a MacroUse there;
        // left unpainted, the grammar's keyword colour stood on a macro.
        if ( isKeyword && type != SemanticTokenType.Macro )
        {
            return null;
        }

        return type;
    }
}
