using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Parser;
using GSCode.Parser.Lexing;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;
using GSCode.Workspace.Api;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// Reports taking the result of a builtin that returns nothing — <c>x = PrintLn( "a" );</c>.
///
/// The value is <c>undefined</c>, so the mistake is silent and surfaces wherever <c>x</c> is next
/// read. The API data has carried <c>BuiltinOverload.ReturnsVoid</c> since the library was written
/// and nothing consumed it.
///
/// Builtins ONLY. A script function's return is not knowable from its signature — GSC has no
/// declared return type, and a function that returns on some paths and falls off the end on others
/// is legal and common — so the same claim about a script function would be a guess.
///
/// Requires EVERY overload to return void. A name with one void overload and one that returns is
/// perfectly usable as a value, and reporting it would flag the correct call.
/// </summary>
public static class VoidResultLint
{
    public static ImmutableArray<Diagnostic> Analyze(ParseResult result, BuiltinApi builtins)
    {
        if ( builtins.Count == 0 )
        {
            return [];
        }

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        foreach ( AstNode element in result.Tree.Root.Elements )
        {
            Walk(element, builtins, diagnostics);
        }

        return diagnostics.ToImmutable();
    }

    private static void Walk(AstNode node, BuiltinApi builtins, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        // The only position that makes the mistake visible: the value is being kept.
        if ( node is AssignmentNode assignment
            && assignment.Operator == TokenKind.Assign
            && assignment.Value is CallNode call
            && ReturnsNothing(call, builtins, out string name) )
        {
            diagnostics.Add(Diagnostic.Create(
                call.Range, DiagnosticSeverity.Warning, GscDiagnosticCode.VoidResultAssigned, name));
        }

        foreach ( AstNode child in AstSearch.ChildrenOf(node) )
        {
            Walk(child, builtins, diagnostics);
        }
    }

    private static bool ReturnsNothing(CallNode call, BuiltinApi builtins, out string name)
    {
        name = "";

        // A qualified or path-qualified callee names a SCRIPT function, which this rule says
        // nothing about. Only a bare name can be a builtin.
        if ( call.Callee is not IdentifierNode callee )
        {
            return false;
        }

        name = callee.Token.Text;
        BuiltinFunction? builtin = builtins.Find(name);
        if ( builtin is null || builtin.Overloads.Length == 0 )
        {
            return false;
        }

        foreach ( BuiltinOverload overload in builtin.Overloads )
        {
            if ( !overload.ReturnsVoid )
            {
                return false;
            }
        }

        return true;
    }
}
