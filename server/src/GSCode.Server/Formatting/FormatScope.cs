using GSCode.Core.Text;
using GSCode.Parser.Lexing;

namespace GSCode.Server.Formatting;

/// <summary>
/// Works out which lines an on-type format is allowed to touch: the alignment GROUP around the
/// cursor, not the whole block.
///
/// A keystroke should tidy what you are working on and stop there. The unit that actually re-flows
/// when you edit a line is its alignment group — the consecutive lines it lines up with — so that
/// is the scope. Editing one of a run of assignments re-aligns that run and halts at the first
/// statement of a different kind; editing one of a run of same-callee calls does the same.
///
/// The rules mirror the aligners so the scope contains exactly what they would change:
/// consecutive assignments form one group (that is what the operator aligner spans), consecutive
/// calls with the same callee form another, a comment on its own line is transparent, and a blank
/// line, a different indent, or a statement of a different kind ends the run.
/// </summary>
public static class FormatScope
{
    /// <summary>The inclusive line range of the alignment group containing <paramref name="line"/>.</summary>
    public static (int Top, int Bottom) GroupAround(string text, int line)
    {
        string[] lines = text.Split('\n');
        if ( lines.Length == 0 )
        {
            return (line, line);
        }

        int here = Math.Clamp(line, 0, lines.Length - 1);

        List<Token>[] byLine = LineFacts.BucketByLine(lines.Length, Lexer.Lex(SourceText.From(text)).Tokens);
        LineKind[] kinds = new LineKind[lines.Length];
        for ( int i = 0; i < lines.Length; i++ )
        {
            kinds[i] = Classify(byLine[i], lines[i]);
        }

        // A line the aligners never touch is scoped to itself: the keystroke fixes that line alone.
        if ( kinds[here].Role != Role.Assignment && kinds[here].Role != Role.Call )
        {
            return (here, here);
        }

        Role role = kinds[here].Role;
        string indent = kinds[here].Indent;
        string key = kinds[here].Key;

        int top = here;
        while ( top > 0 && Continues(kinds[top - 1], role, indent, key) )
        {
            top--;
        }

        int bottom = here;
        while ( bottom < lines.Length - 1 && Continues(kinds[bottom + 1], role, indent, key) )
        {
            bottom++;
        }

        return (top, bottom);
    }

    private static bool Continues(LineKind neighbour, Role role, string indent, string key)
    {
        // A comment on its own line is transparent — it neither joins the group nor ends it.
        if ( neighbour.Role == Role.Comment )
        {
            return true;
        }

        if ( neighbour.Role != role || !string.Equals(neighbour.Indent, indent, StringComparison.Ordinal) )
        {
            return false;
        }

        // Assignments group with any assignment; calls group only with the same callee.
        return role != Role.Call || string.Equals(neighbour.Key, key, StringComparison.Ordinal);
    }

    private enum Role
    {
        Other,
        Comment,
        Assignment,
        Call,
    }

    private readonly record struct LineKind(Role Role, string Indent, string Key);

    private static LineKind Classify(List<Token> lineTokens, string lineText)
    {
        if ( lineTokens.Count == 0 )
        {
            return new LineKind(Role.Other, "", "");
        }

        if ( LineFacts.AllComments(lineTokens) )
        {
            return new LineKind(Role.Comment, "", "");
        }

        List<Token> code = LineFacts.CodeOnly(lineTokens);
        if ( code.Count == 0 || code[^1].Kind != TokenKind.Semicolon )
        {
            return new LineKind(Role.Other, "", "");
        }

        string indent = LineFacts.LeadingWhitespace(lineText);

        // A top-level assignment operator with something before it makes this an assignment.
        int depth = 0;
        int firstParen = -1;
        for ( int i = 0; i < code.Count; i++ )
        {
            TokenKind kind = code[i].Kind;
            switch ( kind )
            {
                case TokenKind.OpenParen:
                case TokenKind.OpenBracket:
                case TokenKind.OpenBrace:
                    if ( kind == TokenKind.OpenParen && firstParen < 0 && depth == 0 )
                    {
                        firstParen = i;
                    }

                    depth++;
                    break;
                case TokenKind.CloseParen:
                case TokenKind.CloseBracket:
                case TokenKind.CloseBrace:
                    depth--;
                    break;
                default:
                    if ( depth == 0 && i > 0 && TokenFacts.IsAssignmentOperator(kind) )
                    {
                        return new LineKind(Role.Assignment, indent, "");
                    }

                    break;
            }
        }

        // Otherwise a statement with a top-level call is grouped by its callee — the text up to the
        // opening parenthesis. Anything else (return, break) is scoped to itself.
        if ( firstParen > 0 )
        {
            int keyEnd = code[firstParen].Range.Start.Character;
            int keyStart = code[0].Range.Start.Character;
            return new LineKind(Role.Call, indent, lineText[keyStart..keyEnd]);
        }

        return new LineKind(Role.Other, "", "");
    }
}
