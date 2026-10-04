using System.Buffers;

namespace GSCode.Parser.Lexing;

/// <summary>
/// What counts as an identifier in GSC, in one place.
///
/// The lexer's own rule is the only one that matters — a name it would split into two tokens is not
/// a name, whatever else thinks so — so this is where that rule lives and the lexer reads it from
/// here rather than keeping a private copy. It is deliberately ASCII: a second, Unicode-flavoured
/// copy of this test had grown up in the server's code-action fixes, and it accepted names the
/// lexer does not.
/// </summary>
public static class GscIdentifier
{
    /// <summary>Every character an identifier may contain after its first.</summary>
    public static readonly SearchValues<char> WordChars = SearchValues.Create(
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_");

    /// <summary>Whether a character may OPEN an identifier — a digit may not.</summary>
    public static bool IsWordStart(char character)
    {
        return char.IsAsciiLetter(character) || character == '_';
    }

    /// <summary>
    /// Whether the whole text is one identifier and nothing else.
    ///
    /// Empty is false rather than a crash: the callers are a rename whose new name comes from a
    /// text box, and a quick fix reading a range out of a buffer that may have moved, so "nothing"
    /// is a reachable input rather than a programming error.
    /// </summary>
    public static bool IsIdentifier(ReadOnlySpan<char> text)
    {
        if ( text.IsEmpty || !IsWordStart(text[0]) )
        {
            return false;
        }

        return !text.ContainsAnyExcept(WordChars);
    }
}
