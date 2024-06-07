using GSCode.Lexer.Types;
using GSCode.Parser.Data;
using GSCode.Parser.SPA.Logic.Components;
using GSCode.Parser.SPA.Sense;

namespace GSCode.Parser.AST.Expressions;

/// <summary>
/// Operation factories govern the grammar of transformations from operators and operands to their completed operations.
/// </summary>
internal interface IOperationFactory
{
    public Func<OperationNode, SymbolTable, ParserIntelliSense, ScrData?, ScrData> EvaluationFunction { get; }

    public bool Matches(List<IExpressionNode> nodes, int index);
}

internal sealed class BinaryOperationFactory : IOperationFactory
{
    public OperatorTypes OperatorType { get; }
    public OperatorOps Operation { get; }

    private static readonly Func<OperationNode, SymbolTable, ParserIntelliSense, ScrData?, ScrData> _defaultEvaluationFunction = (OperationNode _, SymbolTable _, ParserIntelliSense _, ScrData? _) => ScrData.Default;
    public Func<OperationNode, SymbolTable, ParserIntelliSense, ScrData?, ScrData> EvaluationFunction { get; } = _defaultEvaluationFunction;

    public BinaryOperationFactory(OperatorTypes operatorType, OperatorOps operation, Func<OperationNode, SymbolTable, ParserIntelliSense, ScrData?, ScrData>? evaluationFunction = default)
    {
        OperatorType = operatorType;
        Operation = operation;

        if(evaluationFunction is not null)
        {
            EvaluationFunction = evaluationFunction;
        }
    }

    public bool Matches(List<IExpressionNode> nodes, int index)
    {
        if (index + 2 >= nodes.Count)
        {
            return false;
        }

        if (nodes[index + 1] is TokenNode operatorNode &&
            operatorNode.NodeType == ExpressionNodeType.UnresolvedOperator &&
            operatorNode.SourceToken.Is(TokenType.Operator, OperatorType))
        {
            if (!OperatorData.IsOperand(nodes[index]))
            {
                // Add an associated error message to the operator node
                return false;
            }
            if (!OperatorData.IsOperand(nodes[index + 2]))
            {
                // Add an associated error message to the operator node
                return false;
            }

            // Transform the node list
            OperationNode operationNode = new(Operation, nodes[index], nodes[index + 2], nodes[index].Range, EvaluationFunction);
            nodes[index] = operationNode;
            nodes.RemoveRange(index + 1, 2);
            return true;
        }
        return false;
    }
}

internal sealed class PrefixOperationFactory : IOperationFactory
{
    public OperatorTypes OperatorType { get; }
    public OperatorOps Operation { get; }

    private static readonly Func<OperationNode, SymbolTable, ParserIntelliSense, ScrData?, ScrData> _defaultEvaluationFunction = (OperationNode _, SymbolTable _, ParserIntelliSense _, ScrData? _) => ScrData.Default;
    public Func<OperationNode, SymbolTable, ParserIntelliSense, ScrData?, ScrData> EvaluationFunction { get; } = _defaultEvaluationFunction;

    public PrefixOperationFactory(OperatorTypes operatorType, OperatorOps operation, Func<OperationNode, SymbolTable, ParserIntelliSense, ScrData?, ScrData>? evaluationFunction = null)
    {
        OperatorType = operatorType;
        Operation = operation;

        if (evaluationFunction is not null)
        {
            EvaluationFunction = evaluationFunction;
        }
    }

    public bool Matches(List<IExpressionNode> nodes, int index)
    {
        if (index + 1 >= nodes.Count)
        {
            return false;
        }

        if (nodes[index] is TokenNode operatorNode &&
            operatorNode.NodeType == ExpressionNodeType.UnresolvedOperator &&
            operatorNode.SourceToken.Is(TokenType.Operator, OperatorType))
        {
            if (!OperatorData.IsOperand(nodes[index + 1]))
            {
                // Add an associated error message to the operator node
                return false;
            }

            // Transform the node list
            OperationNode operationNode = new(Operation, null, nodes[index + 1], nodes[index].Range, EvaluationFunction);
            nodes[index] = operationNode;
            nodes.RemoveRange(index + 1, 1);
            return true;
        }
        return false;
    }
}

internal sealed class StrictPrefixOperationFactory : IOperationFactory
{
    public OperatorTypes OperatorType { get; }
    public OperatorOps Operation { get; }

    private static readonly Func<OperationNode, SymbolTable, ParserIntelliSense, ScrData?, ScrData> _defaultEvaluationFunction = (OperationNode _, SymbolTable _, ParserIntelliSense _, ScrData? _) => ScrData.Default;
    public Func<OperationNode, SymbolTable, ParserIntelliSense, ScrData?, ScrData> EvaluationFunction { get; } = _defaultEvaluationFunction;

    public StrictPrefixOperationFactory(OperatorTypes operatorType, OperatorOps operation,
        Func<OperationNode, SymbolTable, ParserIntelliSense, ScrData?, ScrData>? evaluationFunction = null)
    {
        OperatorType = operatorType;
        Operation = operation;

        if(evaluationFunction is not null)
        {
            EvaluationFunction = evaluationFunction;
        }
    }

    public bool Matches(List<IExpressionNode> nodes, int index)
    {
        if (index + 1 >= nodes.Count)
        {
            return false;
        }

        if (nodes[index] is TokenNode operatorNode &&
            operatorNode.NodeType == ExpressionNodeType.UnresolvedOperator &&
            operatorNode.SourceToken.Is(TokenType.Operator, OperatorType))
        {
            // Don't pass as a match if a preceding operand is found
            if (index - 1 >= 0 && OperatorData.IsOperand(nodes[index - 1]))
            {
                return false;
            }

            if (!OperatorData.IsOperand(nodes[index + 1]))
            {
                // Add an associated error message to the operator node
                return false;
            }

            // Transform the node list
            OperationNode operationNode = new(Operation, null, nodes[index + 1], nodes[index].Range, EvaluationFunction);
            nodes[index] = operationNode;
            nodes.RemoveRange(index + 1, 1);
            return true;
        }
        return false;
    }
}

internal sealed class PostfixOperationFactory : IOperationFactory
{
    public OperatorTypes OperatorType { get; }
    public OperatorOps Operation { get; }

    private static readonly Func<OperationNode, SymbolTable, ParserIntelliSense, ScrData?, ScrData> _defaultEvaluationFunction = (OperationNode _, SymbolTable _, ParserIntelliSense _, ScrData? _) => ScrData.Default;
    public Func<OperationNode, SymbolTable, ParserIntelliSense, ScrData?, ScrData> EvaluationFunction { get; } = _defaultEvaluationFunction;

    public PostfixOperationFactory(OperatorTypes operatorType, OperatorOps operation)
    {
        OperatorType = operatorType;
        Operation = operation;
    }

    public bool Matches(List<IExpressionNode> nodes, int index)
    {
        if (index + 1 >= nodes.Count)
        {
            return false;
        }

        if (nodes[index + 1] is TokenNode operatorNode &&
            operatorNode.NodeType == ExpressionNodeType.UnresolvedOperator &&
            operatorNode.SourceToken.Is(TokenType.Operator, OperatorType))
        {
            if (!OperatorData.IsOperand(nodes[index]))
            {
                // Add an associated error message to the operator node
                return false;
            }

            // Transform the node list
            OperationNode operationNode = new(Operation, nodes[index], null, nodes[index].Range, EvaluationFunction);
            nodes[index] = operationNode;
            nodes.RemoveRange(index + 1, 1);
            return true;
        }
        return false;
    }
}

internal sealed class EnclosedAccessFactory : IOperationFactory
{
    public OperatorOps Operation { get; }
    public EnclosureType Enclosure { get; }

    private static readonly Func<OperationNode, SymbolTable, ParserIntelliSense, ScrData?, ScrData> _defaultEvaluationFunction = (OperationNode _, SymbolTable _, ParserIntelliSense _, ScrData? _) => ScrData.Default;
    public Func<OperationNode, SymbolTable, ParserIntelliSense, ScrData?, ScrData> EvaluationFunction { get; } = _defaultEvaluationFunction;

    public EnclosedAccessFactory(OperatorOps operation, EnclosureType enclosure, Func<OperationNode, SymbolTable, ParserIntelliSense, ScrData?, ScrData>? evaluationFunction = null)
    {
        Operation = operation;
        Enclosure = enclosure;

        if(evaluationFunction is not null)
        {
            EvaluationFunction = evaluationFunction;
        }
    }

    public bool Matches(List<IExpressionNode> nodes, int index)
    {
        if (index + 1 >= nodes.Count)
        {
            return false;
        }

        if (nodes[index + 1] is EnclosureNode enclosureNode &&
            enclosureNode.EnclosureType == Enclosure &&
            OperatorData.IsFunctionalOperand(nodes[index]))
        {
            // Transform the node list
            OperationNode operationNode = new(Operation, nodes[index], nodes[index + 1], nodes[index].Range, EvaluationFunction);
            nodes[index] = operationNode;
            nodes.RemoveAt(index + 1);
            return true;
        }
        return false;
    }
}

internal sealed class CalledOnFactory : IOperationFactory
{
    public Func<OperationNode, SymbolTable, ParserIntelliSense, ScrData?, ScrData> EvaluationFunction { get; }

    public CalledOnFactory(Func<OperationNode, SymbolTable, ParserIntelliSense, ScrData?, ScrData> evaluationFunction)
    {
        EvaluationFunction = evaluationFunction;
    }

    public bool Matches(List<IExpressionNode> nodes, int index)
    {
        if (index + 1 >= nodes.Count)
        {
            return false;
        }

        // Performs a lookahead to check that there's a function call, if so, create a called on entity operation
        // It's safe to lookahead on i + 1 as any sequence containing . must be dereferenced
        if ((OperatorData.IsOperand(nodes[index]) ||
            (nodes[index] is OperationNode baseNode && 
            (baseNode.Operation == OperatorOps.CalledOnEntity || baseNode.Operation == OperatorOps.FunctionCall ||
            baseNode.Operation == OperatorOps.Subscript || baseNode.Operation == OperatorOps.MemberAccess))) &&
            nodes[index + 1] is OperationNode opNode &&
            (opNode.Operation == OperatorOps.FunctionCall || opNode.Operation == OperatorOps.ThreadedFunctionCall))
        {
            // Transform the node list
            OperationNode operationNode = new(OperatorOps.CalledOnEntity, nodes[index], nodes[index + 1], nodes[index].Range, EvaluationFunction);
            nodes[index] = operationNode;
            nodes.RemoveAt(index + 1);
            return true;
        }
        return false;
    }
}
internal sealed class KeywordOperationFactory : IOperationFactory
{
    public KeywordTypes KeywordType { get; }
    public OperatorOps Operation { get; }

    private static readonly Func<OperationNode, SymbolTable, ParserIntelliSense, ScrData?, ScrData> _defaultEvaluationFunction = (OperationNode _, SymbolTable _, ParserIntelliSense _, ScrData? _) => ScrData.Default;
    public Func<OperationNode, SymbolTable, ParserIntelliSense, ScrData?, ScrData> EvaluationFunction { get; } = _defaultEvaluationFunction;

    public KeywordOperationFactory(KeywordTypes keywordType, OperatorOps operation, 
        Func<OperationNode, SymbolTable, ParserIntelliSense, ScrData?, ScrData> evaluationFunction = null)
    {
        KeywordType = keywordType;
        Operation = operation;

        if(evaluationFunction is not null)
        {
            EvaluationFunction = evaluationFunction;
        }
    }

    public bool Matches(List<IExpressionNode> nodes, int index)
    {
        if (index + 1 >= nodes.Count)
        {
            return false;
        }

        // Performs a lookahead to check that there's a function call, if so, create a called on entity operation
        // It's safe to lookahead on i + 1 as any sequence containing . must be dereferenced
        if (nodes[index] is TokenNode tokenNode && 
            tokenNode.SourceToken.Is(TokenType.Keyword, KeywordType) &&
            nodes[index + 1] is OperationNode opNode &&
            opNode.Operation == OperatorOps.FunctionCall)
        {
            // Transform the node list
            OperationNode operationNode = new(Operation, null, nodes[index + 1], nodes[index].Range, EvaluationFunction);
            nodes[index] = operationNode;
            nodes.RemoveAt(index + 1);
            return true;
        }
        return false;
    }
}
