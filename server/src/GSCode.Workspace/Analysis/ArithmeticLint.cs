using System.Collections.Immutable;
using System.Globalization;
using GSCode.Core.Diagnostics;
using GSCode.Parser.Lexing;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// Division by a divisor that is written as zero.
///
/// 1.5 raised this from its data-flow pass, off a tracked constant VALUE, so it could catch
/// <c>d = 0; x = n / d;</c>. This tree has no constant propagation and does not attempt one: the
/// divisor has to be a literal zero at the point of division, which is the case that needs no
/// analysis at all and is the one a reader can act on without argument.
///
/// Narrower than 1.5's and sound where 1.5's needed a lattice to be. What it gives up is the
/// indirect form; what it gains is that every report is a certainty.
/// </summary>
public static class ArithmeticLint
{
    /// <summary>
    /// This rule's whole judgement about ONE node, with no descent of its own, so
    /// <see cref="NodeLintPass"/> can run it from the shared walk.
    /// </summary>
    internal static void InspectNode(AstNode node, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        switch ( node )
        {
            case BinaryNode { Operator: TokenKind.Slash or TokenKind.Percent } binary
                when IsLiteralZero(binary.Right):
                Report(binary.Right, diagnostics);
                break;

            // `x /= 0` and `x %= 0` divide just as surely as the binary forms.
            case AssignmentNode { Operator: TokenKind.SlashAssign or TokenKind.PercentAssign } assignment
                when IsLiteralZero(assignment.Value):
                Report(assignment.Value, diagnostics);
                break;
        }
    }

    private static void Report(ExprNode divisor, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        diagnostics.Add(Diagnostic.Create(
            divisor.Range,
            DiagnosticSeverity.Warning,
            GscDiagnosticCode.DivisionByZero));
    }

    /// <summary>
    /// Whether the divisor is written as zero — <c>0</c>, <c>0.0</c>, <c>.0</c>, <c>0x0</c>, or any
    /// of those in parentheses.
    ///
    /// Parsed rather than compared as text, so <c>0.00</c> and <c>0x000</c> are caught and a name
    /// that merely starts with a zero is not. A literal that will not parse is left alone: an
    /// unparseable number is the lexer's finding, not this rule's.
    /// </summary>
    private static bool IsLiteralZero(ExprNode node)
    {
        switch ( node )
        {
            case ParenNode paren:
                return IsLiteralZero(paren.Inner);

            case LiteralNode { Token.Kind: TokenKind.Integer } literal:
                return long.TryParse(literal.Token.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
                    && value == 0;

            case LiteralNode { Token.Kind: TokenKind.Float } literal:
                return double.TryParse(literal.Token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                    && number == 0;

            case LiteralNode { Token.Kind: TokenKind.Hex } literal:
                return long.TryParse(
                        literal.Token.Text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex)
                    && hex == 0;

            default:
                return false;
        }
    }
}
