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

        Walk(result.Tree.Root, diagnostics);

        return diagnostics.ToImmutable();
    }

    /// <summary>
    /// Finds every switch in the file. Only <see cref="SwitchNode"/> is interesting, so everything
    /// else descends through <see cref="AstSearch.ChildrenOf"/> rather than being enumerated here —
    /// a statement form added later is walked without this rule having to learn about it.
    ///
    /// The walk stops at an expression. A switch is a STATEMENT and cannot appear inside one, and
    /// the labels are read from the node itself rather than reached by descent, so there is nothing
    /// below an <see cref="ExprNode"/> for this rule to find. Descending anyway walked every operand
    /// of every expression in the file, which expressions outnumber statements by enough to make it
    /// most of the rule's cost.
    /// </summary>
    private static void Walk(AstNode node, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if ( node is ExprNode )
        {
            return;
        }

        if ( node is SwitchNode switchNode )
        {
            InspectLabels(switchNode, diagnostics);
        }

        foreach ( AstNode child in AstSearch.ChildrenOf(node) )
        {
            Walk(child, diagnostics);
        }
    }

    private static void InspectLabels(SwitchNode switchNode, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        // Per SWITCH, not per group: `case 1:` in one group and `case 1:` in another is the same
        // collision, and grouping is a formatting choice rather than a scope.
        HashSet<string> seenLabels = new(StringComparer.Ordinal);

        foreach ( CaseGroupNode group in switchNode.Cases )
        {
            foreach ( CaseLabel label in group.Labels )
            {
                // A null value is `default:`, which has no expression to check.
                if ( label.Value is null )
                {
                    continue;
                }

                Inspect(label.Value, diagnostics);
                InspectDuplicate(label.Value, seenLabels, diagnostics);
            }
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
