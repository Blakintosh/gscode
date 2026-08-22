using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser.Lexing;

namespace GSCode.Parser.Extraction;

/// <summary>
/// Produces semantic highlight tokens for a file, and IDENTIFIERS are the whole of it: each is
/// classified from the extracted reference list as a function, class, macro or field. Keywords,
/// numbers, strings and comments were once emitted from the raw token stream too and no longer are
/// — <see cref="ClassifyToken"/> returns null for every one of them, because a grammar already
/// colours them correctly and a second opinion could only disagree.
///
/// Only single-line tokens are emitted, which the LSP encoding requires. Nothing that survives
/// classification can span a line, so the multi-line split in <see cref="AppendToken"/> is
/// unreachable today; it is kept because the legend still holds slots for the withdrawn kinds and
/// re-adding one would need it back.
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

            AppendToken(tokens, token, type.Value);
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
                return SemanticTokenType.Property;
            default:
                return null;
        }
    }

    private static SemanticTokenType? ClassifyToken(Token token, Dictionary<(int, int), SemanticTokenType> classified)
    {
        switch ( token.Kind )
        {
            // Comments are left to the TextMate grammar, which knows more about them than this
            // does. A semantic token OVERRIDES the grammar's scopes across the range it covers, so
            // painting a whole /@ … @/ block as one Comment flattened the descriptors, argument
            // names and types the grammar colours inside it — the reported "pure commented-out
            // grey". Nothing is lost by standing down: a comment is a comment whatever the
            // surrounding code means, which makes it the one case a grammar answers completely on
            // its own.
            case TokenKind.LineComment:
            case TokenKind.BlockComment:
            case TokenKind.DocComment:
                return null;
            // Same reasoning as comments, and the same visible symptom: the file is coloured by the
            // grammar the moment it opens and REPAINTED when the server's tokens arrive, so any
            // category both describe flickers from one colour to another on startup.
            //
            // A semantic token can only add or repaint — it can never suppress a grammar scope. So
            // for a category the grammar already recognises, the two possibilities are that they
            // agree, in which case the semantic token changes only the shade, or that they
            // disagree, in which case the grammar's colour stands anyway because nothing removed
            // it. Neither outcome is an improvement, and the first is the flicker.
            //
            // That covers every purely LEXICAL category. A string is a string, a number is a
            // number, and `function` is a keyword, by their spelling alone — no surrounding context
            // changes the answer, which is exactly the kind of question a grammar settles.
            case TokenKind.String:
            case TokenKind.LocalizedString:
            case TokenKind.HashString:
            case TokenKind.AnimReference:
            case TokenKind.Integer:
            case TokenKind.Float:
            case TokenKind.Hex:
                return null;
            case TokenKind.Identifier:
            {
                if ( classified.TryGetValue((token.Range.Start.Line, token.Range.Start.Character), out SemanticTokenType type) )
                {
                    return type;
                }

                // Unclassified identifiers fall through to TextMate (default variable styling).
                return null;
            }
            default:
                // Keywords included — see above. What is left for this pass is the one thing a
                // grammar genuinely cannot do: decide what an IDENTIFIER means. Whether `foo` is a
                // function, a macro or a field is a question about the workspace, not about the
                // characters, and that is the whole reason semantic tokens exist.
                //
                // Parameters and locals are NOT among them: SymbolKind has no member for either,
                // so ClassifyReference has nothing to answer with. Their slots in the legend are
                // filled by the WORKSPACE layer instead — LocalReferences.SemanticTokens, merged
                // in by the handler — because a local's identity is (function, name) in the AST,
                // which is knowledge this extraction-level pass does not have.
                return null;
        }
    }

    /// <summary>Splits a (possibly multi-line) token into one semantic token per line it covers.</summary>
    private static void AppendToken(List<SemanticToken> tokens, Token token, SemanticTokenType type)
    {
        TextRange range = token.Range;

        if ( range.Start.Line == range.End.Line )
        {
            tokens.Add(new SemanticToken(range.Start.Line, range.Start.Character, token.Length, type));
            return;
        }

        // Multi-line (block/doc comments): emit the first line to the line's end and each
        // following line from column 0. The exact tail length is cosmetic for comments, so a
        // generous per-line span is fine; clients clamp to the line.
        for ( int line = range.Start.Line; line <= range.End.Line; line++ )
        {
            int startChar = line == range.Start.Line ? range.Start.Character : 0;
            int length = line == range.End.Line ? Math.Max(1, range.End.Character - startChar) : 200;
            tokens.Add(new SemanticToken(line, startChar, length, type));
        }
    }
}
