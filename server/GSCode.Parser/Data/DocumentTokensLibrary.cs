
using System.Text.RegularExpressions;
using GSCode.Parser.Lexical;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Parser.Data;

public sealed class DocumentTokensLibrary
{
    private List<Token> TokenList { get; set; } = new();

    private static readonly Regex IgnoreCommentPattern = new(
        @"^(?://|/\*)\s*(?:gscode|gsc)\s+ignore\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Release the backing token list to allow GC. Used for index-mode scripts after analysis.
    /// </summary>
    public void Clear() => TokenList = [];

    /// <summary>
    /// Returns the set of 0-indexed line numbers that are suppressed by a preceding
    /// `// gscode ignore` / `/* gscode ignore */` (also `gsc ignore`) comment. The suppressed
    /// line is always the line immediately following the comment's end line.
    /// </summary>
    internal HashSet<int> GetIgnoredLines()
    {
        HashSet<int>? ignored = null;
        foreach (Token token in TokenList)
        {
            if (token.Type != TokenType.LineComment && token.Type != TokenType.MultilineComment)
            {
                continue;
            }

            if (!IgnoreCommentPattern.IsMatch(token.Lexeme))
            {
                continue;
            }

            ignored ??= new HashSet<int>();
            ignored.Add(token.TokenRange.EndLine + 1);
        }
        return ignored ?? new HashSet<int>();
    }

    private class TokenKeyComparer : IComparer<Token>
    {
        public int Compare(Token? x, Token? y)
        {
            if (x is null)
            {
                return y is null ? 0 : -1;
            }
            if (y is null)
            {
                return 1;
            }

            int lineComparison = x.TokenRange.StartLine.CompareTo(y.TokenRange.StartLine);
            if (lineComparison != 0)
            {
                return lineComparison;
            }
            return x.TokenRange.StartChar.CompareTo(y.TokenRange.StartChar);
        }
    }

    private static readonly TokenKeyComparer _tokenKeyComparer = new();

    private readonly Token _searchToken = new(TokenType.Unknown, TokenRange.Empty);

    /// <summary>
    /// Returns the index of the token at or before the specified position, or -1 if not found.
    /// Macro expansion body tokens share the macro name's source range and interleave with the
    /// argument tokens in the linked-list order, breaking the sorted invariant assumed by binary
    /// search. When the result lands on a preprocessor-expanded token, we scan both directions
    /// for a real source token whose range contains the requested position.
    /// </summary>
    internal int GetIndex(Position location)
    {
        Token searchToken = _searchToken with { TokenRange = new(location.Line, location.Character, location.Line, location.Character) };

        int index = TokenList.BinarySearch(searchToken, _tokenKeyComparer);

        if (index < 0)
        {
            index = Math.Max(0, ~index - 1);
        }

        if (index >= TokenList.Count)
        {
            return -1;
        }

        // Check if the token at this index actually contains the position.
        // Binary search returns the token that *starts* at or before the position,
        // but we need the token whose range actually contains the position.
        Token tokenAtIndex = TokenList[index];
        if (tokenAtIndex.TokenRange.StartLine == location.Line &&
            tokenAtIndex.TokenRange.EndChar > location.Character &&
            tokenAtIndex.TokenRange.StartChar <= location.Character)
        {
            // Position is within this token's range, great
        }
        else if (index + 1 < TokenList.Count)
        {
            // Try the next token if the current one doesn't contain the position
            Token nextToken = TokenList[index + 1];
            if (nextToken.TokenRange.StartLine == location.Line &&
                nextToken.TokenRange.StartChar <= location.Character &&
                nextToken.TokenRange.EndChar > location.Character)
            {
                index = index + 1;
            }
        }

        // If we landed on a preprocessor-expanded token (body of a macro expansion), scan outward
        // for a real source token that covers the requested position. Argument tokens may be
        // ordered after the expansion body tokens in the linked list even though they appear
        // earlier in the source, so binary search may return the wrong token.
        if (TokenList[index].IsFromPreprocessor)
        {
            // Scan forward for a source token on the requested line/char
            for (int i = index + 1; i < TokenList.Count; i++)
            {
                Token t = TokenList[i];
                if (t.IsFromPreprocessor) continue;
                // Stop if we've gone past the requested line
                if (t.TokenRange.StartLine > location.Line) break;
                if (t.TokenRange.StartLine == location.Line && t.TokenRange.StartChar <= location.Character && t.TokenRange.EndChar > location.Character)
                    return i;
            }
            // Scan backward for a source token
            for (int i = index - 1; i >= 0; i--)
            {
                Token t = TokenList[i];
                if (t.IsFromPreprocessor) continue;
                if (t.TokenRange.StartLine < location.Line) break;
                if (t.TokenRange.StartLine == location.Line && t.TokenRange.StartChar <= location.Character && t.TokenRange.EndChar > location.Character)
                    return i;
            }
        }

        // Selection-end compensation: LSP clients sometimes send the end of an active text
        // selection as the position. That position often lands exactly on the boundary between
        // the selected token and the following one (e.g. right after an identifier, at the start
        // of a ';' or '('). When that happens, every caller of GetIndex/Get would otherwise land
        // on the wrong (non-identifier) token and silently fail. Step back to the preceding
        // identifier so lookups resolve the same way regardless of whether the client sent the
        // selection start or end.
        if (TokenList[index].Type != TokenType.Identifier
            && TokenList[index].TokenRange.StartLine == location.Line
            && TokenList[index].TokenRange.StartChar == location.Character)
        {
            int prevIdx = PrevNonTriviaIndex(index);
            if (prevIdx >= 0 && TokenList[prevIdx].Type == TokenType.Identifier)
            {
                return prevIdx;
            }
        }

        return index;
    }

    /// <summary>
    /// Gets the nearest token at or before the specified position.
    /// </summary>
    internal Token? Get(Position location)
    {
        int index = GetIndex(location);
        return index >= 0 ? TokenList[index] : null;
    }

    /// <summary>
    /// Gets a token by its index in the list, or null if out of bounds.
    /// </summary>
    internal Token? GetAt(int index)
    {
        return (uint)index < (uint)TokenList.Count ? TokenList[index] : null;
    }

    /// <summary>
    /// Returns the index of the next non-whitespace/trivia token after fromIndex, or -1 if none.
    /// </summary>
    internal int NextNonTriviaIndex(int fromIndex)
    {
        for (int i = fromIndex + 1; i < TokenList.Count; i++)
        {
            Token t = TokenList[i];
            if (!t.IsWhitespacey() && !t.IsComment())
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Returns the index of the previous non-whitespace/trivia token before fromIndex, or -1 if none.
    /// </summary>
    internal int PrevNonTriviaIndex(int fromIndex)
    {
        for (int i = fromIndex - 1; i >= 0; i--)
        {
            Token t = TokenList[i];
            if (!t.IsWhitespacey() && !t.IsComment())
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Returns the index of the next non-whitespace token after fromIndex, or -1 if none.
    /// Comments are NOT skipped (only whitespace-like tokens).
    /// </summary>
    internal int NextNonWhitespaceIndex(int fromIndex)
    {
        for (int i = fromIndex + 1; i < TokenList.Count; i++)
        {
            if (!TokenList[i].IsWhitespacey())
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Returns the index of the previous non-whitespace token before fromIndex, or -1 if none.
    /// Comments are NOT skipped (only whitespace-like tokens).
    /// </summary>
    internal int PrevNonWhitespaceIndex(int fromIndex)
    {
        for (int i = fromIndex - 1; i >= 0; i--)
        {
            if (!TokenList[i].IsWhitespacey())
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Finds the index of a specific token by reference equality, using binary search on position
    /// then scanning nearby for the exact reference.
    /// </summary>
    internal int IndexOf(Token token)
    {
        // Binary search to get close to the right position
        int index = TokenList.BinarySearch(token, _tokenKeyComparer);

        if (index < 0)
        {
            index = Math.Max(0, ~index - 1);
        }

        // Scan nearby for exact reference match (tokens at the same position or adjacent)
        int start = Math.Max(0, index - 2);
        int end = Math.Min(TokenList.Count, index + 3);
        for (int i = start; i < end; i++)
        {
            if (ReferenceEquals(TokenList[i], token))
            {
                return i;
            }
        }

        return index;
    }

    /// <summary>
    /// The number of tokens in the library.
    /// </summary>
    internal int Count => TokenList.Count;

    /// <summary>
    /// Once the token list has finished its final transformations, extract tokens into a flat list.
    /// The LinkedToken chain can then be discarded by the caller.
    /// </summary>
    /// <param name="startNode">The first linked token (SOF) in the chain</param>
    internal void AddRange(LinkedToken startNode)
    {
        // Skip SOF and EOF, and tokens spliced in wholesale from an #insert'd file. Those tokens
        // keep the inserted file's own line/char coordinates (needed so macro go-to-definition can
        // jump to the real #define line), which don't correspond to any real position in *this*
        // document and would otherwise break the ascending-position order GetIndex's binary search
        // assumes, sending position-based lookups (completions, hover) after the #insert to the
        // wrong token. The AST parser and SignatureAnalyser walk the raw LinkedToken chain directly
        // (see Script.cs), not this flat list, so excluding these tokens here doesn't affect parsing
        // or signature analysis of #inserted functions/macros - only IDE position lookups, which
        // can never legitimately land on content the host document doesn't actually display.
        LinkedToken? currentNode = startNode.Next;

        while(currentNode is not null && currentNode.Type != TokenType.Eof)
        {
            if (currentNode.Token.InsertSourcePath is null)
            {
                TokenList.Add(currentNode.Token);
            }
            currentNode = currentNode.Next;
        }
        TokenList.TrimExcess();

        // Wire up Previous/Next links on the stored Token objects so navigation methods work.
        for (int i = 0; i < TokenList.Count; i++)
        {
            TokenList[i].Previous = i > 0 ? TokenList[i - 1] : null;
            TokenList[i].Next = i < TokenList.Count - 1 ? TokenList[i + 1] : null;
        }
    }

    internal IEnumerable<Token> GetAll()
    {
        return TokenList;
    }
}
