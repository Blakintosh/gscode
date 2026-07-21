using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Parser;
using GSCode.Parser.Lexing;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;
using GSCode.Workspace.Api;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// Hints that a literal <c>0</c>/<c>1</c> passed to a builtin parameter declared <c>bool</c>
/// should be <c>false</c>/<c>true</c>. Scoped exactly to declared-bool parameters: an int
/// parameter legitimately takes 0 and 1, and flagging those was the v1 bug this rule's
/// original test was written to pin down.
///
/// Overloads must agree. If any overload declares something other than bool at that position,
/// the call is left alone, since which overload the author meant is unknowable here.
/// </summary>
public static class PreferBooleanLiteralLint
{
    public static ImmutableArray<Diagnostic> Analyze(ParseResult result, BuiltinApi builtins)
    {
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        Inspect(result.Tree.Root, builtins, diagnostics);

        return diagnostics.ToImmutable();
    }

    private static void Inspect(AstNode node, BuiltinApi builtins, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if ( node is CallNode call )
        {
            InspectCall(call, builtins, diagnostics);
        }

        foreach ( AstNode child in AstSearch.ChildrenOf(node) )
        {
            Inspect(child, builtins, diagnostics);
        }
    }

    private static void InspectCall(CallNode call, BuiltinApi builtins, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        // Builtins have no namespace, so only a bare identifier callee can name one.
        if ( call.Callee is not IdentifierNode callee )
        {
            return;
        }

        BuiltinFunction? builtin = builtins.Find(callee.Token.Text);
        if ( builtin is null )
        {
            return;
        }

        for ( int index = 0; index < call.Arguments.Length; index++ )
        {
            if ( !IsIntegerZeroOrOne(call.Arguments[index]) )
            {
                continue;
            }

            if ( !AllOverloadsDeclareBool(builtin, index, out string parameterName) )
            {
                continue;
            }

            string replacement = IsOne(call.Arguments[index]) ? "true" : "false";
            diagnostics.Add(Diagnostic.Create(
                call.Arguments[index].Range,
                DiagnosticSeverity.Hint,
                GscDiagnosticCode.PreferBooleanLiteral,
                parameterName,
                replacement));
        }
    }

    /// <summary>
    /// True when every overload declares parameter <paramref name="index"/> as exactly bool.
    /// An exact text match, so int and number never qualify.
    /// </summary>
    private static bool AllOverloadsDeclareBool(BuiltinFunction builtin, int index, out string parameterName)
    {
        parameterName = "";

        if ( builtin.Overloads.Length == 0 )
        {
            return false;
        }

        foreach ( BuiltinOverload overload in builtin.Overloads )
        {
            if ( index >= overload.Parameters.Length )
            {
                return false;
            }

            BuiltinParameter parameter = overload.Parameters[index];
            if ( !string.Equals(parameter.TypeText, "bool", StringComparison.OrdinalIgnoreCase) )
            {
                return false;
            }

            parameterName = parameter.Name;
        }

        return true;
    }

    private static bool IsIntegerZeroOrOne(ExprNode argument)
    {
        if ( argument is not LiteralNode literal || literal.Token.Kind != TokenKind.Integer )
        {
            return false;
        }

        string text = literal.Token.Text;
        return string.Equals(text, "0", StringComparison.Ordinal) || string.Equals(text, "1", StringComparison.Ordinal);
    }

    private static bool IsOne(ExprNode argument)
    {
        return argument is LiteralNode literal && string.Equals(literal.Token.Text, "1", StringComparison.Ordinal);
    }
}
