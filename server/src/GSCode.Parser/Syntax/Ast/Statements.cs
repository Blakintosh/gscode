using System.Collections.Immutable;
using GSCode.Core.Text;
using GSCode.Parser.Preprocessing;

namespace GSCode.Parser.Syntax.Ast;

// The statement-level node family.

/// <summary>{ ... } — also used for implicit single-statement bodies (wrapping one child).</summary>
public sealed record BlockNode(TextRange Range, ImmutableArray<AstNode> Statements) : AstNode(Range);

public sealed record IfNode(TextRange Range, ExprNode Condition, AstNode Then, AstNode? Else) : AstNode(Range);

public sealed record WhileNode(TextRange Range, ExprNode Condition, AstNode Body) : AstNode(Range);

public sealed record DoWhileNode(TextRange Range, AstNode Body, ExprNode Condition) : AstNode(Range);

public sealed record ForNode(TextRange Range, AstNode? Initializer, ExprNode? Condition, AstNode? Increment, AstNode Body) : AstNode(Range);

/// <summary>foreach ( [key,] value in collection ) body — KeyToken is null in the one-variable form.</summary>
public sealed record ForeachNode(TextRange Range, PToken? KeyToken, PToken ValueToken, ExprNode Collection, AstNode Body) : AstNode(Range);

/// <summary>One case/default group: null in Labels marks 'default'; several labels may share a body.</summary>
public sealed record CaseGroupNode(TextRange Range, ImmutableArray<ExprNode?> Labels, ImmutableArray<AstNode> Statements) : AstNode(Range);

public sealed record SwitchNode(TextRange Range, ExprNode Subject, ImmutableArray<CaseGroupNode> Cases) : AstNode(Range);

public sealed record ReturnNode(TextRange Range, ExprNode? Value) : AstNode(Range);

public sealed record BreakNode(TextRange Range) : AstNode(Range);

public sealed record ContinueNode(TextRange Range) : AstNode(Range);

/// <summary>wait expr; and waitrealtime expr; (IsRealTime distinguishes them).</summary>
public sealed record WaitNode(TextRange Range, ExprNode Duration, bool IsRealTime) : AstNode(Range);

public sealed record WaitTillFrameEndNode(TextRange Range) : AstNode(Range);

/// <summary>const NAME = value;</summary>
public sealed record ConstDeclNode(TextRange Range, PToken NameToken, ExprNode Value) : AstNode(Range);

/// <summary>An expression used as a statement (calls, assignments, increments).</summary>
public sealed record ExprStatementNode(TextRange Range, ExprNode Expression) : AstNode(Range);

/// <summary>A statement-level /# ... #/ block.</summary>
public sealed record DevBlockStmtNode(TextRange Range, ImmutableArray<AstNode> Statements) : AstNode(Range);

/// <summary>A lone ';' — legal and ignored.</summary>
public sealed record EmptyStatementNode(TextRange Range) : AstNode(Range);
