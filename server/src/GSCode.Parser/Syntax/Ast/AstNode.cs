using GSCode.Core.Text;

namespace GSCode.Parser.Syntax.Ast;

/// <summary>
/// Base of every syntax-tree node. Range is in ROOT-file coordinates (macro-expanded and
/// inserted content collapses onto its root site), so outline/diagnostic consumers can
/// use it directly; true locations of names come from their PTokens' provenance.
/// </summary>
public abstract record AstNode(TextRange Range);

/// <summary>Base of every expression node.</summary>
public abstract record ExprNode(TextRange Range) : AstNode(Range);

/// <summary>A node standing in for unparseable source; the tree always covers the file.</summary>
public sealed record ErrorNode(TextRange Range) : ExprNode(Range);
