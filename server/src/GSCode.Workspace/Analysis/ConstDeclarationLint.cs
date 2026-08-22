using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Parser;
using GSCode.Parser.Lexing;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// The two things a <c>const</c> declaration promises: that its value is known at compile time
/// (5029), and that nothing later changes it (5030).
///
/// Both were 1.5 diagnostics, and neither needs type information — which is why they were separable
/// from the family they were removed alongside. <c>ConstDeclNode</c> carries the name token and the
/// value expression, so 5029 is structural recursion over the value and 5030 is a set of names
/// checked against assignment targets.
///
/// No dialect gate. <c>const</c> is a keyword only in Black Ops III, so no earlier game's parse tree
/// contains a <c>ConstDeclNode</c> to inspect and the rule stands down by having nothing to look at.
/// This deliberately does NOT extend to the Infinity Ward dialects' file-scope constants: those are
/// our modelling of a bare <c>NAME = 1.0;</c> between two functions, and nothing establishes that
/// the engine refuses a later write to one. Reporting them would be asserting an immutability the
/// language may not have.
///
/// What counts as constant was measured rather than guessed. Black Ops III's stock scripts hold 117
/// <c>const</c> declarations; every one is a literal or arithmetic over literals — <c>64 * 64</c>,
/// <c>40.0 * 40.0</c>, <c>.5</c> — so operators over constants have to be accepted or the rule
/// reports shipped code.
/// </summary>
public static class ConstDeclarationLint
{
    public static ImmutableArray<Diagnostic> Analyze(ParseResult result)
    {
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        Walk(result.Tree.Root, diagnostics);

        foreach ( AstNode element in result.Tree.Root.Elements )
        {
            InspectDeclaration(element, diagnostics);
        }

        return diagnostics.ToImmutable();
    }

    // --- 5029: the value has to be known at compile time ---

    private static void Walk(AstNode node, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if ( node is ConstDeclNode constDecl && !IsConstantExpression(constDecl.Value) )
        {
            diagnostics.Add(Diagnostic.Create(
                constDecl.Value.Range,
                DiagnosticSeverity.Warning,
                GscDiagnosticCode.ExpectedConstantExpression,
                constDecl.NameToken.Text));
        }

        foreach ( AstNode child in AstSearch.ChildrenOf(node) )
        {
            Walk(child, diagnostics);
        }
    }

    /// <summary>
    /// Whether an expression can be folded before the script runs.
    ///
    /// A macro's expansion arrives already substituted, so a value that came from one is judged on
    /// whatever it expanded to — the same rule <see cref="CaseLabelLint"/> applies to case labels,
    /// and for the same reason: the squiggle would otherwise point into an expansion rather than at
    /// anything the author wrote.
    /// </summary>
    private static bool IsConstantExpression(ExprNode node)
    {
        switch ( node )
        {
            case LiteralNode:
            case ArrayLiteralNode:
                return true;

            case ParenNode paren:
                return IsConstantExpression(paren.Inner);

            case VectorNode vector:
                return IsConstantExpression(vector.X)
                    && IsConstantExpression(vector.Y)
                    && IsConstantExpression(vector.Z);

            // `&my_func` resolves to a function at compile time, so it is as constant as a literal.
            // Accepted rather than reported because the cost of being wrong runs the wrong way here:
            // a missed real mistake is invisible, a false Error on a working pointer is not.
            case PrefixNode { Operator: TokenKind.Ampersand }:
                return true;

            case PrefixNode prefix:
                return IsConstantExpression(prefix.Operand);

            // `64 * 64` and `40.0 * 40.0` are both in the stock scripts. An operator over constants
            // is still a constant.
            case BinaryNode binary:
                return IsConstantExpression(binary.Left) && IsConstantExpression(binary.Right);

            default:
                return false;
        }
    }

    // --- 5030: nothing may assign to it afterwards ---

    /// <summary>
    /// Per FUNCTION, which the corpus insisted on. Collecting the names file-wide looks harmless —
    /// how often is a constant's name reused? — and reported ten writes across Black Ops III's
    /// shipped scripts, every one an ordinary local in a DIFFERENT function that happened to share
    /// the name: <c>scripts\zm\gametypes\_hud_message.gsc</c> declares <c>const duration</c> in one
    /// function and assigns a plain <c>duration</c> in four others, and
    /// <c>vehicle_death_shared.gsc</c> does the same with <c>max_angluar_vel</c>.
    ///
    /// A function body is the whole scope needed. Black Ops III's <c>const</c> is a statement, so
    /// there is no file-scope form of it for a second function to see.
    /// </summary>
    private static void InspectDeclaration(AstNode element, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        switch ( element )
        {
            case FunctionNode function:
                InspectBody(function.Body, diagnostics);
                return;

            case ConstructorNode constructor:
                InspectBody(constructor.Body, diagnostics);
                return;

            case DestructorNode destructor:
                InspectBody(destructor.Body, diagnostics);
                return;

            case ClassNode classNode:
                foreach ( AstNode member in classNode.Members )
                {
                    InspectDeclaration(member, diagnostics);
                }

                return;

            case DevBlockDeclNode devBlock:
                foreach ( AstNode declaration in devBlock.Declarations )
                {
                    InspectDeclaration(declaration, diagnostics);
                }

                return;
        }
    }

    private static void InspectBody(AstNode body, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        HashSet<string> constants = new(StringComparer.OrdinalIgnoreCase);
        CollectConstantNames(body, constants);

        if ( constants.Count == 0 )
        {
            return;
        }

        InspectAssignments(body, constants, diagnostics);
    }

    private static void CollectConstantNames(AstNode node, HashSet<string> constants)
    {
        if ( node is ConstDeclNode constDecl )
        {
            constants.Add(constDecl.NameToken.Text);
        }

        foreach ( AstNode child in AstSearch.ChildrenOf(node) )
        {
            CollectConstantNames(child, constants);
        }
    }

    private static void InspectAssignments(
        AstNode node, HashSet<string> constants, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        switch ( node )
        {
            // Only a bare name. `X[ 0 ] = v` and `X.f = v` write THROUGH the constant rather than to
            // it, and whether that is legal is a question about the value's type, which this rule
            // does not have and will not guess at.
            case AssignmentNode { Target: IdentifierNode target }:
                Report(target, constants, diagnostics);
                break;

            case PostfixNode { Operand: IdentifierNode operand }:
                Report(operand, constants, diagnostics);
                break;
        }

        foreach ( AstNode child in AstSearch.ChildrenOf(node) )
        {
            InspectAssignments(child, constants, diagnostics);
        }
    }

    private static void Report(
        IdentifierNode target, HashSet<string> constants, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if ( !constants.Contains(target.Token.Text) )
        {
            return;
        }

        diagnostics.Add(Diagnostic.Create(
            target.Range,
            DiagnosticSeverity.Warning,
            GscDiagnosticCode.CannotAssignToConstant,
            target.Token.Text));
    }
}
