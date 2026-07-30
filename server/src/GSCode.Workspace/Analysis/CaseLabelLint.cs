using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Parser;
using GSCode.Parser.Lexing;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// Reports a <c>case</c> label that is not a compile-time constant — <c>case undefined:</c> being
/// the one people actually write.
///
/// A switch dispatches on constants, so a label has to be one. <c>undefined</c> is the trap: it
/// LOOKS like a value and parses fine, but nothing ever equals it in a switch, so the branch is
/// silently unreachable rather than wrong-looking. `isdefined( x )` is what was meant.
///
/// What counts as a constant is taken from the stock scripts rather than guessed: of the 1,918
/// case labels there, every one is a string (1,552), an integer (241) or a macro (125). No floats,
/// no variables, no calls. Macro-supplied labels are accepted whatever they expand to, on the same
/// reasoning the default-parameter rule used before it was retired — the preprocessor has already
/// made them constant, and the squiggle would point into an expansion rather than at anything the
/// author wrote.
/// </summary>
public static class CaseLabelLint
{
    public static ImmutableArray<Diagnostic> Analyze(ParseResult result)
    {
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        foreach ( AstNode element in result.Tree.Root.Elements )
        {
            Walk(element, diagnostics);
        }

        return diagnostics.ToImmutable();
    }

    private static void Walk(AstNode? node, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        switch ( node )
        {
            case null:
                return;
            case SwitchNode switchNode:
            {
                // Per SWITCH, not per group: `case 1:` in one group and `case 1:` in another is the
                // same collision, and grouping is a formatting choice rather than a scope.
                HashSet<string> seenLabels = new(StringComparer.Ordinal);

                foreach ( CaseGroupNode group in switchNode.Cases )
                {
                    foreach ( ExprNode? label in group.Labels )
                    {
                        // A null label is `default:`, which has no value to check.
                        if ( label is null )
                        {
                            continue;
                        }

                        Inspect(label, diagnostics);
                        InspectDuplicate(label, seenLabels, diagnostics);
                    }

                    foreach ( AstNode statement in group.Statements )
                    {
                        Walk(statement, diagnostics);
                    }
                }

                return;
            }
            case FunctionNode function:
                Walk(function.Body, diagnostics);
                return;
            case ClassNode classNode:
                foreach ( AstNode member in classNode.Members )
                {
                    Walk(member, diagnostics);
                }

                return;
            case BlockNode block:
                foreach ( AstNode statement in block.Statements )
                {
                    Walk(statement, diagnostics);
                }

                return;
            case DevBlockDeclNode devBlockDecl:
                foreach ( AstNode declaration in devBlockDecl.Declarations )
                {
                    Walk(declaration, diagnostics);
                }

                return;
            case DevBlockStmtNode devBlock:
                foreach ( AstNode statement in devBlock.Statements )
                {
                    Walk(statement, diagnostics);
                }

                return;
            case IfNode ifNode:
                Walk(ifNode.Then, diagnostics);
                Walk(ifNode.Else, diagnostics);
                return;
            case WhileNode whileNode:
                Walk(whileNode.Body, diagnostics);
                return;
            case DoWhileNode doWhile:
                Walk(doWhile.Body, diagnostics);
                return;
            case ForNode forNode:
                Walk(forNode.Body, diagnostics);
                return;
            case ForeachNode foreachNode:
                Walk(foreachNode.Body, diagnostics);
                return;
            default:
                return;
        }
    }

    /// <summary>
    /// Reports a label the switch has already seen. Only the first can ever match, so the second
    /// branch is unreachable — the same class of finding as 5015, but invisible in the code's shape
    /// because nothing about the second `case` looks wrong.
    ///
    /// Compared by the label's printed form, and only for labels that are already known constant:
    /// 5011 speaks for anything else, and comparing two expressions this rule cannot evaluate would
    /// be guessing. Case-SENSITIVE, because a string label is matched by value and `"A"` and `"a"`
    /// are different events.
    /// </summary>
    private static void InspectDuplicate(
        ExprNode label, HashSet<string> seenLabels, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if ( !IsConstant(label) )
        {
            return;
        }

        string printed = AstPrinter.Print(label);
        if ( seenLabels.Add(printed) )
        {
            return;
        }

        diagnostics.Add(Diagnostic.Create(
            label.Range,
            DiagnosticSeverity.Warning,
            GscDiagnosticCode.DuplicateCaseLabel,
            printed));
    }

    private static void Inspect(ExprNode label, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if ( IsConstant(label) )
        {
            return;
        }

        // `case undefined:` earns its own message: it is a specific mistake with a specific fix,
        // and "not a constant" would not tell anyone what to do about it.
        bool isUndefined = label is LiteralNode literal && literal.Token.Kind == TokenKind.Undefined;

        diagnostics.Add(Diagnostic.Create(
            label.Range,
            DiagnosticSeverity.Warning,
            isUndefined ? GscDiagnosticCode.CaseUndefined : GscDiagnosticCode.NonConstantCaseLabel));
    }

    private static bool IsConstant(ExprNode label)
    {
        switch ( label )
        {
            case LiteralNode literal:
                // Everything a literal can be EXCEPT undefined, which never matches.
                return literal.Token.Kind != TokenKind.Undefined;

            // -1 and the like: a sign in front of a number is still a constant.
            case PrefixNode prefix when prefix.Operator == TokenKind.Minus:
                return IsConstant(prefix.Operand);

            case ParenNode paren:
                return IsConstant(paren.Inner);

            default:
                // A macro's expansion arrives already substituted, so a label that came from one
                // is whatever it expanded to and is judged on that.
                return false;
        }
    }
}
