using GSCode.Parser.Lexing;

namespace GSCode.Parser.Preprocessing;

/// <summary>
/// Evaluates a #if/#elif condition over already-macro-expanded tokens. The grammar
/// matches the engine's (verified against v1): || then &amp;&amp; chains, single ==/!= and
/// relational applications, parenthesized subexpressions, INTEGER literals only.
/// No defined(), no arithmetic. An unparseable condition yields null → branch inactive.
/// </summary>
public static class ConditionalEvaluator
{
    /// <summary>
    /// Parenthesis nesting this evaluator will follow. It is a second recursive descent, over the
    /// condition's tokens rather than over the tree, so the parser's own ceiling does not reach it:
    /// <c>#if ((((…</c> overflowed the stack on its own at fewer than 3,000 parentheses. A condition
    /// the engine would accept is nested a handful deep at most, and 64 matches the depth cap used
    /// elsewhere in the server (ClassCycleLint, MethodResolution).
    /// </summary>
    private const int MaxDepth = 64;

    /// <summary>Evaluates the condition; null means it could not be resolved to an integer.</summary>
    public static int? Evaluate(IReadOnlyList<PToken> tokens)
    {
        int position = 0;
        // Trailing tokens after a parsed expression are ignored, matching engine behavior.
        return OrExpression(tokens, ref position, 0);
    }

    private static int? OrExpression(IReadOnlyList<PToken> tokens, ref int position, int depth)
    {
        int? left = AndExpression(tokens, ref position, depth);
        if ( left is null )
        {
            return null;
        }

        int result = left.Value;
        while ( position < tokens.Count && tokens[position].Kind == TokenKind.LogicalOr )
        {
            position++;
            int? right = AndExpression(tokens, ref position, depth);
            if ( right is null )
            {
                return null;
            }

            result = (result != 0 || right.Value != 0) ? 1 : 0;
        }

        return result;
    }

    private static int? AndExpression(IReadOnlyList<PToken> tokens, ref int position, int depth)
    {
        int? left = EqualityExpression(tokens, ref position, depth);
        if ( left is null )
        {
            return null;
        }

        int result = left.Value;
        while ( position < tokens.Count && tokens[position].Kind == TokenKind.LogicalAnd )
        {
            position++;
            int? right = EqualityExpression(tokens, ref position, depth);
            if ( right is null )
            {
                return null;
            }

            result = (result != 0 && right.Value != 0) ? 1 : 0;
        }

        return result;
    }

    private static int? EqualityExpression(IReadOnlyList<PToken> tokens, ref int position, int depth)
    {
        int? left = RelationalExpression(tokens, ref position, depth);
        if ( left is null )
        {
            return null;
        }

        if ( position >= tokens.Count )
        {
            return left;
        }

        TokenKind operatorKind = tokens[position].Kind;
        if ( operatorKind != TokenKind.EqualsEquals && operatorKind != TokenKind.NotEquals )
        {
            return left;
        }

        position++;
        int? right = RelationalExpression(tokens, ref position, depth);
        if ( right is null )
        {
            return null;
        }

        if ( operatorKind == TokenKind.EqualsEquals )
        {
            return left.Value == right.Value ? 1 : 0;
        }

        return left.Value != right.Value ? 1 : 0;
    }

    private static int? RelationalExpression(IReadOnlyList<PToken> tokens, ref int position, int depth)
    {
        int? left = Primary(tokens, ref position, depth);
        if ( left is null )
        {
            return null;
        }

        if ( position >= tokens.Count )
        {
            return left;
        }

        TokenKind operatorKind = tokens[position].Kind;
        bool isRelational = operatorKind is TokenKind.LessThan
            or TokenKind.LessThanEquals
            or TokenKind.GreaterThan
            or TokenKind.GreaterThanEquals;

        if ( !isRelational )
        {
            return left;
        }

        position++;
        int? right = Primary(tokens, ref position, depth);
        if ( right is null )
        {
            return null;
        }

        switch ( operatorKind )
        {
            case TokenKind.LessThan:
                return left.Value < right.Value ? 1 : 0;
            case TokenKind.LessThanEquals:
                return left.Value <= right.Value ? 1 : 0;
            case TokenKind.GreaterThan:
                return left.Value > right.Value ? 1 : 0;
            default:
                return left.Value >= right.Value ? 1 : 0;
        }
    }

    private static int? Primary(IReadOnlyList<PToken> tokens, ref int position, int depth)
    {
        if ( position >= tokens.Count )
        {
            return null;
        }

        PToken current = tokens[position];

        if ( current.Kind == TokenKind.OpenParen )
        {
            // Too deep is simply a condition this cannot resolve, which the caller already handles:
            // the branch goes inactive, exactly as it does for a macro that never expanded.
            if ( depth >= MaxDepth )
            {
                return null;
            }

            position++;
            int? inner = OrExpression(tokens, ref position, depth + 1);
            if ( inner is null )
            {
                return null;
            }

            if ( position >= tokens.Count || tokens[position].Kind != TokenKind.CloseParen )
            {
                return null;
            }

            position++;
            return inner;
        }

        if ( current.Kind == TokenKind.Integer && int.TryParse(current.Text, out int value) )
        {
            position++;
            return value;
        }

        // Anything else (identifiers that never expanded, strings, ...) fails the condition.
        return null;
    }
}
