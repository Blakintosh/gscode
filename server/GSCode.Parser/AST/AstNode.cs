using GSCode.Data;
using GSCode.Parser.Lexical;
using System.Diagnostics.CodeAnalysis;
using GSCode.Parser.DFA;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Parser.AST;

internal enum AstNodeType
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
    WaitTillFrameEndStmt,
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
    ArgsList,
    Primitive,
    ClassDefinition,
    ClassMember,
    Constructor,
    Destructor,
}

internal enum ExprOperatorType
{
    DataOperand,
    IdentifierOperand,
    Ternary,
    Binary,
    Vector,
    Prefix,
    Postfix,
    MethodCall,
    FunctionCall,
    Indexer,
    CallOn
}

internal abstract class AstNode(AstNodeType nodeType)
{
    public AstNodeType NodeType { get; } = nodeType;
}

internal sealed class ScriptNode() : AstNode(AstNodeType.Script)
{
    public required List<DependencyNode> Dependencies { get; init; }
    public required List<AstNode> ScriptDefns { get; init; }
}

[method: SetsRequiredMembers]
internal sealed class DependencyNode(PathNode input) : AstNode(AstNodeType.Dependency)
{
    public required string Path { get; init; } = string.Join(System.IO.Path.DirectorySeparatorChar, input.Segments);
    public Range Range { get; init; } = RangeHelper.From(input.First!.Range.Start, input.Last!.Range.End);
    public Token FirstPathToken { get; init; } = input.First!;
}
internal sealed class PathNode(Token? last = null) : AstNode(AstNodeType.Temporary)
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

internal sealed class PrecacheNode() : AstNode(AstNodeType.Precache)
{
    public required string Type { get; init; }
    public required Range TypeRange { get; init; }

    public required string Path { get; init; }
    public required Range PathRange { get; init; }
}

internal sealed class UsingAnimTreeNode(Token nameToken) : AstNode(AstNodeType.UsingAnimTree)
{
    public string Name { get; } = nameToken.Lexeme;
    public Range Range { get; } = nameToken.Range;
}

internal sealed class NamespaceNode(Token namespaceToken) : AstNode(AstNodeType.Namespace)
{
    public string NamespaceIdentifier { get; } = namespaceToken.Lexeme;
    public Range Range { get; } = namespaceToken.Range;
}

internal sealed class FunKeywordsNode() : AstNode(AstNodeType.Temporary)
{
    public LinkedList<Token> Keywords { get; } = new();
}

internal sealed class FunDefnNode() : AstNode(AstNodeType.FunctionDefinition)
{
    public required Token? Name { get; init; }
    public required FunKeywordsNode Keywords { get; init; }
    public required ParamListNode Parameters { get; init; }
    public required StmtListNode Body { get; init; }
}

internal sealed class ParamListNode(LinkedList<ParamNode>? parameters = null, bool vararg = false) : AstNode(AstNodeType.ParameterList)
{
    public LinkedList<ParamNode> Parameters { get; } = parameters ?? [];
    public bool Vararg { get; set; } = vararg;
}

internal sealed class ParamNode(Token? name, bool byRef, ExprNode? defaultNode = null) : AstNode(AstNodeType.Parameter)
{
    public Token? Name { get; } = name;
    public bool ByRef { get; } = byRef;
    public ExprNode? Default { get; } = defaultNode;
}

internal sealed class ClassBodyListNode(LinkedList<AstNode>? definitions = null) : AstNode(AstNodeType.BraceBlock)
{
    public LinkedList<AstNode> Definitions { get; } = definitions ?? new();
}

internal sealed class StmtListNode(LinkedList<AstNode>? statements = null) : AstNode(AstNodeType.BraceBlock)
{
    public LinkedList<AstNode> Statements { get; } = statements ?? new();
}

internal sealed class EmptyStmtNode() : AstNode(AstNodeType.EmptyStmt) { }

internal sealed class IfStmtNode() : AstNode(AstNodeType.IfStmt)
{
    public ExprNode? Condition { get; init; }
    public AstNode? Then { get; init; }
    public IfStmtNode? Else { get; set; }
}

internal sealed class ReservedFuncStmtNode(AstNodeType type, ExprNode? expr) : AstNode(type)
{
    public ExprNode? Expr { get; } = expr;
}

internal sealed class ConstStmtNode(Token identifierToken, ExprNode? value) : AstNode(AstNodeType.ConstStmt)
{
    public string Identifier { get; } = identifierToken.Lexeme;
    public Range Range { get; } = RangeHelper.From(identifierToken.Range.Start, value.Range.End);
    public ExprNode? Value { get; } = value;
}

internal sealed class ExprStmtNode(ExprNode? expr) : AstNode(AstNodeType.ExprStmt)
{
    public ExprNode? Expr { get; } = expr;
}

internal sealed class DoWhileStmtNode(ExprNode? condition, AstNode? then) : AstNode(AstNodeType.DoWhileStmt)
{
    public ExprNode? Condition { get; } = condition;
    public AstNode? Then { get; } = then;
}

internal sealed class WhileStmtNode(ExprNode? condition, AstNode? then) : AstNode(AstNodeType.WhileStmt)
{
    public ExprNode? Condition { get; } = condition;
    public AstNode? Then { get; } = then;
}

internal sealed class ForStmtNode(AstNode? init, ExprNode? condition, AstNode? increment, AstNode? then) : AstNode(AstNodeType.ForStmt)
{
    public AstNode? Init { get; } = init;
    public ExprNode? Condition { get; } = condition;
    public AstNode? Increment { get; } = increment;
    public AstNode? Then { get; } = then;
}

internal sealed class ForeachStmtNode(Token valueIdentifier, Token? keyIdentifier, ExprNode? collection, AstNode? then) : AstNode(AstNodeType.ForeachStmt)
{
    public Token? KeyIdentifier { get; } = keyIdentifier;
    public Token ValueIdentifier { get; } = valueIdentifier;
    public ExprNode? Collection { get; } = collection;
    public AstNode? Then { get; } = then;
}

internal sealed class ControlFlowActionNode(AstNodeType type, Token actionToken) : AstNode(type)
{
    public Range Range { get; } = actionToken.Range;
}

internal sealed class ReturnStmtNode(ExprNode? value = default) : AstNode(AstNodeType.ReturnStmt)
{
    public ExprNode? Value { get; } = value;
}

internal sealed class DefnDevBlockNode(List<AstNode> definitions) : AstNode(AstNodeType.DevBlock)
{
    public List<AstNode> Definitions { get; } = definitions;
}

internal sealed class FunDevBlockNode(StmtListNode body) : AstNode(AstNodeType.DevBlock)
{
    public StmtListNode Body { get; } = body;
}

internal sealed class SwitchStmtNode() : AstNode(AstNodeType.SwitchStmt)
{
    public ExprNode? Expression { get; init; }
    public required CaseListNode Cases { get; init; }
}

internal sealed class CaseListNode() : AstNode(AstNodeType.CaseList)
{
    public LinkedList<CaseStmtNode> Cases { get; } = new();
}

internal sealed class CaseStmtNode() : AstNode(AstNodeType.CaseStmt)
{
    public LinkedList<CaseLabelNode> Labels { get; } = new();
    public required StmtListNode Body { get; init; }
}

internal sealed class CaseLabelNode(AstNodeType labelType, ExprNode? value = default) : AstNode(labelType)
{
    public ExprNode? Value { get; } = value;
}

internal abstract class ExprNode(ExprOperatorType operatorType, Range range) : AstNode(AstNodeType.Expr)
{
    public Range Range { get; } = range;

    public ExprOperatorType OperatorType { get; } = operatorType;
}

internal sealed class DataExprNode : ExprNode
{
    public object? Value { get; }
    public ScrDataTypes Type { get; }

    private DataExprNode(object? value, ScrDataTypes dataType, Range range) : base(ExprOperatorType.DataOperand, range)
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
                // TODO: temp - addresses issue with int overflow on 2147483648 without further information on why this is happening yet
                // TODO: this is a thing in util_shared, and it's unclear what the intended behaviour is. Validate and confirm later, then
                // undo the long change if possible.
                TokenType.Integer => new(long.Parse(token.Lexeme), ScrDataTypes.Int, token.Range),
                TokenType.Hex => new(long.Parse(token.Lexeme[2..], System.Globalization.NumberStyles.HexNumber),
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
                $"Failed to parse primitive token, which suggests that the lexer is not producing valid tokens. The intention was: {token.Lexeme}, for type {token.Type}.",
                ex);
        }
    }

    public static DataExprNode EmptyArray(Token openBracket, Token closeBracket)
    {
        return new(new List<object>(), ScrDataTypes.Array, RangeHelper.From(openBracket.Range.Start, closeBracket.Range.End));
    }
}

// TODO: this is not ideal because it doesn't include the parentheses in the range.
internal sealed class VectorExprNode(ExprNode x, ExprNode? y, ExprNode? z)
    : ExprNode(ExprOperatorType.Vector, RangeHelper.From(x.Range.Start, z?.Range.End ?? y?.Range.End ?? x.Range.End))
{
    public ExprNode X { get; } = x;
    public ExprNode? Y { get; } = y;
    public ExprNode? Z { get; } = z;

}

// TODO: this is not ideal because it doesn't include the parentheses in the range.
internal sealed class TernaryExprNode(ExprNode condition, ExprNode? then, ExprNode? @else)
    : ExprNode(ExprOperatorType.Ternary, RangeHelper.From(condition.Range.Start, @else?.Range.End ?? then?.Range.End ?? condition.Range.End))
{
    public ExprNode Condition { get; } = condition;
    public ExprNode? Then { get; } = then;
    public ExprNode? Else { get; } = @else;
}

internal sealed class IdentifierExprNode(Token identifier) : ExprNode(ExprOperatorType.IdentifierOperand, identifier.Range)
{
    public Token Token { get; } = identifier;
    public bool IsAnim { get; } = identifier.Type == TokenType.AnimIdentifier;

    public string Identifier { get; } = identifier.Lexeme;
}

internal sealed class BinaryExprNode(ExprNode? left, Token operatorToken, ExprNode? right)
    : ExprNode(
        ExprOperatorType.Binary,
        RangeHelper.From(left?.Range.Start ?? operatorToken.Range.Start, right?.Range.End ?? operatorToken.Range.End))
{
    public ExprNode? Left { get; } = left;
    public Token Operator { get; } = operatorToken;
    public ExprNode? Right { get; } = right;

    public TokenType Operation => Operator.Type;
}

internal sealed class PrefixExprNode(Token operatorToken, ExprNode operand)
    : ExprNode(ExprOperatorType.Prefix, RangeHelper.From(operatorToken.Range.Start, operand.Range.End))
{
    public Token Operator { get; } = operatorToken;
    public ExprNode Operand { get; } = operand;

    public TokenType Operation => Operator.Type;
}

internal sealed class PostfixExprNode(ExprNode operand, Token operatorToken)
    : ExprNode(ExprOperatorType.Postfix, RangeHelper.From(operand.Range.Start, operatorToken.Range.End))
{
    public ExprNode Operand { get; } = operand;
    public Token Operator { get; } = operatorToken;

    public TokenType Operation => Operator.Type;
}

// TODO: might need to include the whole range (ie new + the brackets)
internal sealed class ConstructorExprNode(Token identifierToken)
    : ExprNode(ExprOperatorType.FunctionCall, identifierToken.Range)
{
    public Token Identifier { get; } = identifierToken;
}

internal sealed class MethodCallNode(Position firstTokenPosition, ExprNode? objectTarget, Token methodToken, ArgsListNode arguments)
    : ExprNode(ExprOperatorType.FunctionCall, RangeHelper.From(firstTokenPosition, arguments.Range.End))
{
    public ExprNode? Target { get; } = objectTarget;
    public Token Method { get; } = methodToken;
    public ArgsListNode Arguments { get; } = arguments;
}

internal sealed class FunCallNode(Position startPosition, ExprNode? target, ArgsListNode arguments)
    : ExprNode(ExprOperatorType.FunctionCall, RangeHelper.From(startPosition, arguments.Range.End))
{
    public ExprNode? Target { get; } = target;
    public ArgsListNode Arguments { get; } = arguments;

    public FunCallNode(ExprNode target, ArgsListNode arguments)
        : this(target.Range.Start, target, arguments) { }
}

internal sealed class NamespacedMemberNode(ExprNode @namespace, ExprNode member)
    : ExprNode(ExprOperatorType.Binary, RangeHelper.From(@namespace.Range.Start, member.Range.End))
{
    public ExprNode Namespace { get; } = @namespace;
    public ExprNode Member { get; } = member;
}

internal sealed class ArgsListNode(LinkedList<ExprNode?>? arguments = null) : AstNode(AstNodeType.ArgsList)
{
    public LinkedList<ExprNode?> Arguments { get; } = arguments ?? [];
    public Range Range { get; set; } = default!;
}

internal sealed class ArrayIndexNode(Range range, ExprNode array, ExprNode? index) : ExprNode(ExprOperatorType.Indexer, range)
{
    public ExprNode Array { get; } = array;
    public ExprNode? Index { get; } = index;
}

internal sealed class CalledOnNode(ExprNode on, ExprNode call) : ExprNode(ExprOperatorType.CallOn, RangeHelper.From(on.Range.Start, call.Range.End))
{
    public ExprNode On { get; } = on;
    public ExprNode Call { get; } = call;
}

internal class ClassDefnNode(Token? nameToken, Token? inheritsFromToken, ClassBodyListNode body) : AstNode(AstNodeType.ClassDefinition)
{
    public Token? NameToken { get; } = nameToken;
    public Token? InheritsFromToken { get; } = inheritsFromToken;

    public ClassBodyListNode Body { get; } = body;
}

internal class MemberDeclNode(Token? nameToken) : AstNode(AstNodeType.ClassMember)
{
    public Token? NameToken { get; } = nameToken;
}

internal class StructorDefnNode(Token keywordToken, StmtListNode body)
    : AstNode(keywordToken.Type == TokenType.Constructor ? AstNodeType.Constructor : AstNodeType.Destructor)
{
    public Token KeywordToken { get; } = keywordToken;
    public StmtListNode Body { get; } = body;
}