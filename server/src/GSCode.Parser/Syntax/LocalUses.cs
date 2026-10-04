using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Parser.Syntax;

/// <summary>How one site uses a local variable's name.</summary>
public enum LocalUseKind
{
    /// <summary>The value is read.</summary>
    Read,

    /// <summary><c>x = v</c>, or a <c>const</c> declaration: the name is written and nothing else.</summary>
    Assign,

    /// <summary><c>x += v</c>: written, but the old value is read to do it.</summary>
    CompoundAssign,

    /// <summary>
    /// The name an indexed or member target is rooted at: <c>a</c> in <c>a[ i ] = v</c> or
    /// <c>a.f = v</c>. A write, because <c>a[ 0 ] = x</c> CREATES <c>a</c> when it does not exist —
    /// that is how a GSC array is built — but it also uses whatever <c>a</c> already held.
    /// </summary>
    ElementAssign,

    /// <summary>A <c>foreach</c> key or value, which the loop writes each pass.</summary>
    LoopBinding,

    /// <summary>
    /// A <c>waittill</c> output — <c>attacker</c> in <c>self waittill( "damage", attacker )</c>. The
    /// engine fills it in; the first argument is the event NAME and is an ordinary read.
    /// </summary>
    EventBinding,
}

/// <summary>One use of a name the body walk found, in source order.</summary>
public readonly record struct LocalUse(PToken Token, LocalUseKind Kind)
{
    /// <summary>Whether the site writes the name, in any of the ways a write happens.</summary>
    public bool IsWrite
    {
        get { return Kind != LocalUseKind.Read; }
    }
}

/// <summary>
/// The one walk that says where a function body reads and writes its local variables. Find
/// references, rename, highlight, local semantic tokens, <c>UnassignedVariableLint</c> and
/// <c>UnusedLocalLint</c> each had their own copy; they must agree about what a use of a local is,
/// so the classification lives here and each caller only decides what the kinds mean to it.
///
/// Descends through <see cref="AstSearch.ChildrenOf"/> rather than a switch over every node kind:
/// the interesting nodes are few — assignments, the binding forms, and the places an identifier is
/// not a variable at all — and a node type added later is traversed without this having to learn
/// about it.
/// </summary>
public static class LocalUses
{
    public static List<LocalUse> Of(AstNode body)
    {
        List<LocalUse> uses = [];
        Collect(body, uses);
        return uses;
    }

    private static void Collect(AstNode node, List<LocalUse> uses)
    {
        switch ( node )
        {
            case AssignmentNode assignment:
                if ( assignment.Target is IdentifierNode target )
                {
                    LocalUseKind kind = assignment.Operator == TokenKind.Assign
                        ? LocalUseKind.Assign
                        : LocalUseKind.CompoundAssign;
                    uses.Add(new LocalUse(target.Token, kind));
                }
                else
                {
                    CollectElementTarget(assignment.Target, uses);
                }

                Collect(assignment.Value, uses);
                return;

            case ForeachNode foreachNode:
                if ( foreachNode.KeyToken is not null )
                {
                    uses.Add(new LocalUse(foreachNode.KeyToken.Value, LocalUseKind.LoopBinding));
                }

                uses.Add(new LocalUse(foreachNode.ValueToken, LocalUseKind.LoopBinding));
                Collect(foreachNode.Collection, uses);
                Collect(foreachNode.Body, uses);
                return;

            case ConstDeclNode constDecl:
                uses.Add(new LocalUse(constDecl.NameToken, LocalUseKind.Assign));
                Collect(constDecl.Value, uses);
                return;

            case IdentifierNode identifier:
                uses.Add(new LocalUse(identifier.Token, LocalUseKind.Read));
                return;

            case MemberNode member:
                // `a.b` reads `a`; `b` is a field name, with a life of its own another script may read.
                Collect(member.Object, uses);
                return;

            case CallNode call:
            {
                // The callee of `foo()` names a FUNCTION, not a local spelled foo; `[[ handler ]]()`
                // really does read the local. Target is what the call is made ON — `self` in
                // `self foo()` — which is a value.
                if ( call.Callee is not (IdentifierNode or QualifiedNode or PathQualifiedNode) )
                {
                    Collect(call.Callee, uses);
                }

                if ( call.Target is not null )
                {
                    Collect(call.Target, uses);
                }

                bool bindsOutputs = AstSearch.IsWaittill(call.Callee);
                for ( int index = 0; index < call.Arguments.Length; index++ )
                {
                    if ( bindsOutputs && index > 0 && call.Arguments[index] is IdentifierNode bound )
                    {
                        uses.Add(new LocalUse(bound.Token, LocalUseKind.EventBinding));
                        continue;
                    }

                    Collect(call.Arguments[index], uses);
                }

                return;
            }

            case PrefixNode prefix when prefix.Operator == TokenKind.Ampersand:
                // `&foo` is a pointer to a FUNCTION, not a use of a variable.
                return;

            default:
                foreach ( AstNode child in AstSearch.ChildrenOf(node) )
                {
                    Collect(child, uses);
                }

                return;
        }
    }

    /// <summary>
    /// An indexed or member assignment target: the name it is rooted at is an
    /// <see cref="LocalUseKind.ElementAssign"/>, while every subscript along the way is read —
    /// <c>a[ i ] = x</c> genuinely reads <c>i</c>.
    /// </summary>
    private static void CollectElementTarget(ExprNode target, List<LocalUse> uses)
    {
        switch ( target )
        {
            case IdentifierNode identifier:
                uses.Add(new LocalUse(identifier.Token, LocalUseKind.ElementAssign));
                return;

            case IndexNode index:
                CollectElementTarget(index.Object, uses);
                Collect(index.Index, uses);
                return;

            case MemberNode member:
                CollectElementTarget(member.Object, uses);
                return;

            default:
                // A call result or a deref introduces no name, so the ordinary read rules apply.
                Collect(target, uses);
                return;
        }
    }
}
