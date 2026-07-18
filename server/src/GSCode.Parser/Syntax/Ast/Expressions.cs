using System.Collections.Immutable;
using GSCode.Core.Text;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;

namespace GSCode.Parser.Syntax.Ast;

// The expression node family.

/// <summary>Any single-token literal: numbers, strings (all three kinds), anim refs, true/false/undefined, #animtree.</summary>
public sealed record LiteralNode(TextRange Range, PToken Token) : ExprNode(Range);

/// <summary>A bare identifier reference (also carries keyword-callee tokens like waittill).</summary>
public sealed record IdentifierNode(TextRange Range, PToken Token) : ExprNode(Range);

/// <summary>ns::name — a namespace-qualified function/class reference.</summary>
public sealed record QualifiedNode(TextRange Range, PToken NamespaceToken, PToken NameToken) : ExprNode(Range);

/// <summary>( inner ) — kept explicit so ranges and the formatter stay faithful.</summary>
public sealed record ParenNode(TextRange Range, ExprNode Inner) : ExprNode(Range);

/// <summary>( x, y, z ) — a vector literal.</summary>
public sealed record VectorNode(TextRange Range, ExprNode X, ExprNode Y, ExprNode Z) : ExprNode(Range);

/// <summary>[] — the empty array literal.</summary>
public sealed record ArrayLiteralNode(TextRange Range) : ExprNode(Range);

public sealed record BinaryNode(TextRange Range, ExprNode Left, TokenKind Operator, ExprNode Right) : ExprNode(Range);

public sealed record TernaryNode(TextRange Range, ExprNode Condition, ExprNode WhenTrue, ExprNode WhenFalse) : ExprNode(Range);

/// <summary>Prefix operators: ! ~ - and &amp; (function address-of).</summary>
public sealed record PrefixNode(TextRange Range, TokenKind Operator, ExprNode Operand) : ExprNode(Range);

/// <summary>Postfix ++ and --.</summary>
public sealed record PostfixNode(TextRange Range, ExprNode Operand, TokenKind Operator) : ExprNode(Range);

/// <summary>target OP= value (OP= includes plain '=').</summary>
public sealed record AssignmentNode(TextRange Range, ExprNode Target, TokenKind Operator, ExprNode Value) : ExprNode(Range);

/// <summary>obj.field access.</summary>
public sealed record MemberNode(TextRange Range, ExprNode Object, PToken NameToken) : ExprNode(Range);

/// <summary>obj[index] access.</summary>
public sealed record IndexNode(TextRange Range, ExprNode Object, ExprNode Index) : ExprNode(Range);

/// <summary>[[ pointer ]] — function-pointer dereference (two adjacent brackets each side).</summary>
public sealed record PointerDerefNode(TextRange Range, ExprNode Pointer) : ExprNode(Range);

/// <summary>
/// Any call: Callee is an Identifier/Qualified/PointerDeref node. Target is the
/// method-notation object (ent foo()); IsThread marks thread / ent thread forms.
/// </summary>
public sealed record CallNode(
    TextRange Range,
    ExprNode? Target,
    bool IsThread,
    ExprNode Callee,
    ImmutableArray<ExprNode> Arguments) : ExprNode(Range);

/// <summary>[[obj]]->method(args) — a class method call.</summary>
public sealed record ArrowCallNode(
    TextRange Range,
    PointerDerefNode Object,
    PToken MethodToken,
    ImmutableArray<ExprNode> Arguments) : ExprNode(Range);

/// <summary>new ClassName().</summary>
public sealed record NewNode(TextRange Range, PToken ClassToken, ImmutableArray<ExprNode> Arguments) : ExprNode(Range);
