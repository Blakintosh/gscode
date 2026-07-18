using System.Collections.Immutable;
using GSCode.Core.Text;
using GSCode.Parser.Preprocessing;

namespace GSCode.Parser.Syntax.Ast;

// The declaration-level node family. Grouped in one file because these are pure data
// declarations that read best side by side.

/// <summary>The root: every top-level element in source order (namespace state is positional).</summary>
public sealed record ScriptNode(TextRange Range, ImmutableArray<AstNode> Elements) : AstNode(Range);

/// <summary>#using scripts\foo\bar; — Path is the joined, unnormalized text.</summary>
public sealed record UsingNode(TextRange Range, string Path, TextRange PathRange) : AstNode(Range);

/// <summary>#namespace name; — changes the namespace state for everything below it.</summary>
public sealed record NamespaceNode(TextRange Range, PToken NameToken) : AstNode(Range);

/// <summary>#precache(type, value, ...); — arguments kept raw for P4's table validation.</summary>
public sealed record PrecacheNode(TextRange Range, ImmutableArray<PToken> Arguments) : AstNode(Range);

/// <summary>#using_animtree("name");</summary>
public sealed record UsingAnimTreeNode(TextRange Range, PToken? TreeNameToken) : AstNode(Range);

/// <summary>One function parameter: name, optional &amp; by-ref marker, optional default value.</summary>
public sealed record ParameterNode(TextRange Range, PToken NameToken, bool ByRef, ExprNode? DefaultValue) : AstNode(Range);

/// <summary>A function declaration (top-level or class method).</summary>
public sealed record FunctionNode(
    TextRange Range,
    PToken NameToken,
    bool IsPrivate,
    bool IsAutoexec,
    ImmutableArray<ParameterNode> Parameters,
    bool HasVarargs,
    BlockNode Body) : AstNode(Range);

/// <summary>class Name [: Parent] { ... } — members are VarDecl/Function/Constructor/Destructor nodes.</summary>
public sealed record ClassNode(
    TextRange Range,
    PToken NameToken,
    PToken? ParentToken,
    ImmutableArray<AstNode> Members) : AstNode(Range);

/// <summary>var name; inside a class body.</summary>
public sealed record VarDeclNode(TextRange Range, PToken NameToken) : AstNode(Range);

/// <summary>constructor() { ... } — parameters are parsed (for diagnostics) but illegal.</summary>
public sealed record ConstructorNode(TextRange Range, PToken KeywordToken, ImmutableArray<ParameterNode> Parameters, BlockNode Body) : AstNode(Range);

/// <summary>destructor() { ... } — parameters are parsed (for diagnostics) but illegal.</summary>
public sealed record DestructorNode(TextRange Range, PToken KeywordToken, ImmutableArray<ParameterNode> Parameters, BlockNode Body) : AstNode(Range);

/// <summary>A top-level /# ... #/ block wrapping whole declarations.</summary>
public sealed record DevBlockDeclNode(TextRange Range, ImmutableArray<AstNode> Declarations) : AstNode(Range);
