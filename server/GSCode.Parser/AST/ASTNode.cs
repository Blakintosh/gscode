using GSCode.Data;
using GSCode.Parser.Lexer;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.AST;

internal enum ASTNodeType
{
    Script,
    Dependency,
    Precache,
    UsingAnimTree,
    Namespace,
    FunctionDefinition,
    Temporary,
    ParameterList,
    Parameter,
    BraceBlock,
    EmptyStmt,
    IfStmt,
    ElseStmt
}

internal abstract class ASTNode(ASTNodeType nodeType)
{
    public ASTNodeType NodeType { get; } = nodeType;
}

internal sealed class ScriptNode() : ASTNode(ASTNodeType.Script)
{
    public required List<DependencyNode> Dependencies { get; init; }
    public required List<ASTNode> ScriptDefns { get; init; }
}

[method: SetsRequiredMembers]
internal sealed class DependencyNode(PathNode input) : ASTNode(ASTNodeType.Dependency)
{
    public required string Path { get; init; } = string.Join(System.IO.Path.DirectorySeparatorChar, input.Segments);
    public Range Range { get; init; } = RangeHelper.From(input.First!.Range.Start, input.Last!.Range.End);
}

internal sealed class PathNode(Token? last = null) : ASTNode(ASTNodeType.Temporary)
{
    public LinkedList<string> Segments { get; } = new();

    public Token? First { get; private set; } = last;
    public Token? Last { get; private set; } = last;

    public void PrependSegment(Token segment)
    {
        Last ??= segment;

        First = segment;
        Segments.AddFirst(segment.Lexeme);
    }
}

internal sealed class PrecacheNode() : ASTNode(ASTNodeType.Precache)
{
    public required string Type { get; init; }
    public required Range TypeRange { get; init; }

    public required string Path { get; init; }
    public required Range PathRange { get; init; }
}

internal sealed class UsingAnimTreeNode(Token nameToken) : ASTNode(ASTNodeType.UsingAnimTree)
{
    public string Name { get; } = nameToken.Lexeme;
    public Range Range { get; } = nameToken.Range;
}

internal sealed class NamespaceNode(Token namespaceToken) : ASTNode(ASTNodeType.Namespace)
{
    public string NamespaceIdentifier { get; } = namespaceToken.Lexeme;
    public Range Range { get; } = namespaceToken.Range;
}

internal sealed class FunKeywordsNode() : ASTNode(ASTNodeType.Temporary)
{
    public LinkedList<Token> Keywords { get; } = new();
}

internal sealed class FunDefnNode() : ASTNode(ASTNodeType.FunctionDefinition)
{
    public required Token Name { get; init; }
    public required FunKeywordsNode Keywords { get; init; }
    public required ParamListNode Parameters { get; init; }
    public required BlockNode Body { get; init; }
}

internal sealed class ParamListNode(LinkedList<ParamNode>? parameters = null, bool vararg = false) : ASTNode(ASTNodeType.ParameterList)
{
    public LinkedList<ParamNode> Parameters { get; } = parameters ?? [];
    public bool Vararg { get; set; } = vararg;
}

internal sealed class ParamNode(Token? name, bool byRef, ExprNode? defaultNode = null) : ASTNode(ASTNodeType.Parameter)
{
    public Token? Name { get; } = name;
    public bool ByRef { get; } = byRef;
    public ExprNode? Default { get; } = defaultNode;
}

internal sealed class StmtListNode(LinkedList<ASTNode>? statements = null) : ASTNode(ASTNodeType.BraceBlock)
{
    public LinkedList<ASTNode> Statements { get; } = statements ?? new();
}

internal sealed class EmptyStmtNode() : ASTNode(ASTNodeType.EmptyStmt) {}

internal sealed class IfElseStmtNode() : ASTNode(ASTNodeType.IfStmt)
{
    public ExprNode Condition { get; init; }
    public ASTNode Then { get; init; }
    public ElseStmtNode? Else { get; init; }
}

internal sealed class ElseStmtNode() : ASTNode(ASTNodeType.ElseStmt)
{
    public ExprNode? Condition { get; init; }
    public ASTNode Then { get; init; }
    public ElseStmtNode? Else { get; init; }
}