﻿using GSCode.Data;
using GSCode.Parser.Lexer;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GSCode.Parser.DFA;

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
    WaitStmt,
    WaitRealTimeStmt,
    ConstStmt,
    ExprStmt,
    DoWhileStmt,
    WhileStmt,
    ForStmt,
    ForeachStmt,
    WaittillFrameEndStmt,
    BreakStmt,
    ContinueStmt,
    ReturnStmt,
    DevBlock,
    SwitchStmt,
    CaseList,
    CaseStmt,
    CaseLabel, 
    DefaultLabel,
    Expr,
    Primitive
}

internal enum ExprOperatorType
{
    Operand,
    Ternary,
    Binary,
    Vector,
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

internal sealed class IfStmtNode() : ASTNode(ASTNodeType.IfStmt)
{
    public ExprNode? Condition { get; init; }
    public ASTNode Then { get; init; }
    public IfStmtNode? Else { get; set; }
}

internal sealed class ReservedFuncStmtNode(ASTNodeType type, ExprNode expr) : ASTNode(type)
{
    public ExprNode? Expr { get; } = expr;
}

internal sealed class ConstStmtNode(Token identifierToken, ExprNode value) : ASTNode(ASTNodeType.ConstStmt)
{
    public string Identifier { get; } = identifierToken.Lexeme;
    public Range Range { get; } = RangeHelper.From(identifierToken.Range.Start, value.Range.End);
    public ExprNode Value { get; } = value;
}

internal sealed class ExprStmtNode(ExprNode expr) : ASTNode(ASTNodeType.ExprStmt)
{
    public ExprNode Expr { get; } = expr;
}

internal sealed class DoWhileStmtNode(ExprNode condition, ASTNode then) : ASTNode(ASTNodeType.DoWhileStmt)
{
    public ExprNode Condition { get; } = condition;
    public ASTNode Then { get; } = then;
}

internal sealed class WhileStmtNode(ExprNode condition, ASTNode then) : ASTNode(ASTNodeType.WhileStmt)
{
    public ExprNode Condition { get; } = condition;
    public ASTNode Then { get; } = then;
}

internal sealed class ForStmtNode(ASTNode? init, ExprNode? condition, ASTNode? increment, ASTNode then) : ASTNode(ASTNodeType.ForStmt)
{
    public ASTNode? Init { get; } = init;
    public ExprNode? Condition { get; } = condition;
    public ASTNode? Increment { get; } = increment;
    public ASTNode Then { get; } = then;
}

internal sealed class ForeachStmtNode(Token identifier, ExprNode collection, ASTNode then) : ASTNode(ASTNodeType.ForeachStmt)
{
    public string Identifier { get; } = identifier.Lexeme;
    public ExprNode Collection { get; } = collection;
    public ASTNode Then { get; } = then;
}

internal sealed class ControlFlowActionNode(ASTNodeType type, Token actionToken) : ASTNode(type)
{
    public Range Range { get; } = actionToken.Range;
}

internal sealed class ReturnStmtNode(ExprNode? value = default) : ASTNode(ASTNodeType.ReturnStmt)
{
    public ExprNode? Value { get; } = value;
}

internal sealed class DevBlockNode(StmtListNode body) : ASTNode(ASTNodeType.DevBlock)
{
    public StmtListNode Body { get; } = body;
}

internal sealed class SwitchStmtNode() : ASTNode(ASTNodeType.SwitchStmt)
{
    public ExprNode? Expression { get; init; }
    public CaseListNode Cases { get; init; }
}

internal sealed class CaseListNode() : ASTNode(ASTNodeType.CaseList)
{
    public LinkedList<CaseStmtNode> Cases { get; } = new();
}

internal sealed class CaseStmtNode() : ASTNode(ASTNodeType.CaseStmt)
{
    public LinkedList<CaseLabelNode> Labels { get; } = new();
    public required StmtListNode Body { get; init; }
}

internal sealed class CaseLabelNode(ASTNodeType labelType, ExprNode? value = default) : ASTNode(labelType)
{
    public ExprNode? Value { get; } = value;
}

internal abstract class ExprNode(ExprOperatorType operatorType, Range range) : ASTNode(ASTNodeType.Expr)
{
    public Range Range { get; } = range;
    
    public ExprOperatorType OperatorType { get; } = operatorType;
}

internal sealed class AssignmentExprNode(ExprOperatorType operatorType, Token identifier) 
    : ExprNode(operatorType, identifier.Range)
{
    public string Identifier { get; } = identifier.Lexeme;
}

internal sealed class DataExprNode : ExprNode
{
    public object? Value { get; }
    public ScrDataTypes Type { get; }

    private DataExprNode(object? value, ScrDataTypes dataType, Range range) : base(ExprOperatorType.Operand, range)
    {
        Value = value;
        Type = dataType;
    }

    public static DataExprNode From(Token token)
    {
        try
        {
            return token.Type switch
            {
                // Numbers
                TokenType.Float => new(float.Parse(token.Lexeme), ScrDataTypes.Float, token.Range),
                TokenType.Integer => new(int.Parse(token.Lexeme), ScrDataTypes.Int, token.Range),
                TokenType.Hex => new(int.Parse(token.Lexeme[2..], System.Globalization.NumberStyles.HexNumber),
                    ScrDataTypes.Int, token.Range),
                // Strings - remove quotes
                TokenType.String => new(token.Lexeme[1..^1], ScrDataTypes.String, token.Range),
                TokenType.IString => new(token.Lexeme[2..^1], ScrDataTypes.IString, token.Range),
                TokenType.CompilerHash => new(token.Lexeme[2..^1], ScrDataTypes.Hash, token.Range),
                // Booleans and undefined
                TokenType.True => new(true, ScrDataTypes.Bool, token.Range),
                TokenType.False => new(false, ScrDataTypes.Bool, token.Range),
                TokenType.Undefined => new(null, ScrDataTypes.Undefined, token.Range),
                // AnimTree
                TokenType.AnimTree => new(token.Lexeme, ScrDataTypes.AnimTree, token.Range),
                // ...
                _ => throw new ArgumentOutOfRangeException(nameof(token.Type), token.Type, null)
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new Exception(
                "Failed to parse primitive token, which suggests that the lexer is not producing valid tokens.",
                ex);
        }
    }

    public static DataExprNode EmptyArray(Token openBracket, Token closeBracket)
    {
        return new(new List<object>(), ScrDataTypes.Array, RangeHelper.From(openBracket.Range.Start, closeBracket.Range.End));
    }
}

// TODO: this is not ideal because it doesn't include the parentheses in the range.
internal sealed class VectorExprNode(ExprNode x, ExprNode y, ExprNode z)
    : ExprNode(ExprOperatorType.Vector, RangeHelper.From(x.Range.Start, z.Range.End))
{
    public ExprNode X { get; } = x;
    public ExprNode Y { get; } = y;
    public ExprNode Z { get; } = z;

}

// TODO: this is not ideal because it doesn't include the parentheses in the range.
internal sealed class TernaryExprNode(ExprNode condition, ExprNode then, ExprNode @else)
    : ExprNode(ExprOperatorType.Ternary, RangeHelper.From(condition.Range.Start, @else.Range.End))
{
    public ExprNode Condition { get; } = condition;
    public ExprNode Then { get; } = then;
    public ExprNode Else { get; } = @else;
}

internal sealed class IdentifierExprNode(Token identifier) : ExprNode(ExprOperatorType.Operand, identifier.Range)
{
    public string Identifier { get; } = identifier.Lexeme;
}