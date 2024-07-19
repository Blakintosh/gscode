using GSCode.Data;
using GSCode.Parser.Data;
using GSCode.Parser.DFA;
using GSCode.Parser.SPA.Logic.Components;

namespace GSCode.Parser.AST.Expressions
{
    internal enum ExpressionNodeType
    {
        // Errors
        Unknown,
        // TokenNode
        Literal,
        Field,
        UnresolvedOperator,
        UnresolvedPunctuation,
        // OperationNode
        Operation,
        // EnclosureNode
        Enclosure,
        // Empty (used when no expression is in the final result)
        Empty
    }

    internal enum EnclosureType
    {
        Parenthesis,
        Dereference,
        Bracket
    }

    internal interface IExpressionNode
    {
        public ExpressionNodeType NodeType { get; }

        public Range Range { get; }
    }

    internal sealed record TokenNode(ExpressionNodeType NodeType, Token SourceToken) : IExpressionNode
    {
        public Range Range => SourceToken.TextRange;
    }

    internal sealed record EnclosureNode(List<IExpressionNode> InteriorNodes, EnclosureType EnclosureType, Range Range) : IExpressionNode
    {
        public ExpressionNodeType NodeType { get; } = ExpressionNodeType.Enclosure;
    }

    internal sealed record OperationNode(OperatorOps Operation, IExpressionNode? Left, IExpressionNode? Right, Range Range, Func<OperationNode, SymbolTable, ParserIntelliSense, ScrData?, ScrData> EvaluationFunction) : IExpressionNode
    {
        public ScrData? ParentLhs { get; set; }
        public Range Range
        {
            get
            {
                Range? leftRange = Left?.Range;
                Range? rightRange = Right?.Range;

                if(leftRange is not null && rightRange is not null)
                {
                    return RangeHelper.From(leftRange.Start, rightRange.End);
                }
                else if(leftRange is not null)
                {
                    return leftRange;
                }

                return rightRange!;
            }
        }
        public TokenNode FarRightTokenLeaf
        {
            get
            {
                if (Right is TokenNode tokenNode)
                {
                    return tokenNode;
                }
                else if(Right is OperationNode operationNode)
                {
                    return operationNode.FarRightTokenLeaf;
                }
                throw new NotSupportedException("RightmostNode cannot be called on operations where the right-hand operands are not of token-like type.");
            }
        }

        public ExpressionNodeType NodeType { get; } = ExpressionNodeType.Operation;
    }

    internal sealed record EmptyNode() : IExpressionNode
    {
        public ExpressionNodeType NodeType { get; } = ExpressionNodeType.Empty;
        public Range Range => throw new NotSupportedException();
        public IExpressionNode RightmostNode => throw new NotSupportedException();
    }
}
