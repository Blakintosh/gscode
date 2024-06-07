using GSCode.Data;
using GSCode.Lexer.Types;
using GSCode.Parser.AST.Expressions;
using GSCode.Parser.Data;
using GSCode.Parser.SPA.Logic.Components;
using GSCode.Parser.SPA.Sense;
using Serilog.Context;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.SPA.Logic.Analysers;

internal class FunctionSignatureAnalyzer
{
    public static List<ScrParameter>? Analyze(Expression expression, ParserIntelliSense sense)
    {
        // No data if failed
        if(expression.Failed)
        {
            return null;
        }

        // If empty
        if(expression.Empty)
        {
            return new();
        }

        return AnalyseNode(expression.Root!, sense);
    }

    public static List<ScrParameter>? AnalyseNode(IExpressionNode node, ParserIntelliSense sense)
    {
        return node.NodeType switch
        {
            ExpressionNodeType.Unknown => null,
            // Not valid in arg list
            ExpressionNodeType.Literal => AnalyseLiteral((TokenNode)node, sense),
            ExpressionNodeType.Enclosure => IssueBadParameterError(node, sense),
            // Possibly valid
            ExpressionNodeType.Field => AnalyseField((TokenNode)node, sense),
            ExpressionNodeType.Operation => AnalyseOperation((OperationNode)node, sense),
            _ => throw new InvalidOperationException($"Unsupported node type: {node.NodeType}"),
        };
    }

    public static List<ScrParameter>? AnalyseOperation(OperationNode node, ParserIntelliSense sense)
    {
        if(node.Operation != OperatorOps.Comma &&
            node.Operation != OperatorOps.Assign &&
            node.Operation != OperatorOps.AddressOf)
        {
            return IssueBadParameterError(node, sense);
        }

        // Base case when has a default value.
        if(node.Operation == OperatorOps.Assign)
        {
            ScrParameter? result = AnalyseParameterWithDefault(node, sense);

            if (result != null)
            {
                return new()
                {
                    result
                };
            }
        }
        // Another base case is a passed by reference value.
        else if(node.Operation == OperatorOps.AddressOf &&
            node.Right is TokenNode)
        {
            ScrParameter? result = AnalyseParameter((TokenNode)node.Right, sense);
            if (result != null)
            {
                return new()
                {
                    result
                };
            }
        }
        // TODO: This is currently O(n), could be better by using a LL
        else if(node.Operation == OperatorOps.Comma)
        {
            List<ScrParameter>? leftResult = AnalyseNode(node.Left!, sense);
            List<ScrParameter>? rightResult = AnalyseNode(node.Right!, sense);

            if(leftResult != null && rightResult != null)
            {
                // Edge case: Error if vararg (...) is not the very last parameter. Simultaneously doesn't allow for multiple varargs to be put in.
                if(HasVararg(leftResult))
                {
                    sense.AddSpaDiagnostic(leftResult[leftResult.Count - 1].Range, GSCErrorCodes.VarargNotLastParameter);
                    return null;
                }

                leftResult.AddRange(rightResult);
                return leftResult;
            }
        }

        return null;
    }

    public static List<ScrParameter>? AnalyseLiteral(TokenNode node, ParserIntelliSense sense)
    {
        if(node.SourceToken.Is(TokenType.Keyword, KeywordTypes.Vararg))
        {
            return new()
            {
                new ScrParameter
                {
                    // only vararg can legally have this name
                    Name = "vararg",
                    Type = ScrDataTypes.Array,
                    Range = node.SourceToken.TextRange
                }
            };
        }

        return IssueBadParameterError(node, sense);
    }

    public static List<ScrParameter>? AnalyseField(TokenNode node, ParserIntelliSense sense)
    {
        ScrParameter? result = AnalyseParameter(node, sense);

        if (result != null)
        {
            return new()
            {
                result
            };
        }
        return null;
    }

    public static ScrParameter? AnalyseParameterWithDefault(OperationNode node, ParserIntelliSense sense)
    {
        // Check LHS has been given as a simple parameter name
        if(node.Left is not TokenNode tokenNode ||
            tokenNode.NodeType != ExpressionNodeType.Field)
        {
            sense.AddSpaDiagnostic(node.Range, GSCErrorCodes.IdentifierExpected);
            return null;
        }

        // Don't allow the user to use vararg as a parameter name
        if(tokenNode.SourceToken.Contents == "vararg")
        {
            sense.AddSpaDiagnostic(node.Range, GSCErrorCodes.ParameterNameReserved, "vararg");
            return null;
        }

        return new ScrParameter()
        {
            Name = tokenNode.SourceToken.Contents,
            Type = ScrDataTypes.Unknown,
            // We don't evaluate defaults here, but do record where they are.
            DefaultNode = node.Right,
            Range = tokenNode.SourceToken.TextRange
        };
    }

    public static ScrParameter? AnalyseParameter(TokenNode node, ParserIntelliSense sense)
    {
        // Check has been given as a simple parameter name
        if (node.NodeType != ExpressionNodeType.Field)
        {
            sense.AddSpaDiagnostic(node.Range, GSCErrorCodes.IdentifierExpected);
            return null;
        }

        // Don't allow the user to use vararg as a parameter name
        if (node.SourceToken.Contents == "vararg")
        {
            sense.AddSpaDiagnostic(node.Range, GSCErrorCodes.ParameterNameReserved, "vararg");
            return null;
        }

        return new ScrParameter()
        {
            Name = node.SourceToken.Contents,
            Type = ScrDataTypes.Unknown,
            Range = node.SourceToken.TextRange
        };
    }

    private static List<ScrParameter>? IssueBadParameterError(IExpressionNode node, ParserIntelliSense sense)
    {
        sense.AddSpaDiagnostic(node.Range, GSCErrorCodes.IdentifierExpected);
        return null;
    }

    private static bool HasVararg(List<ScrParameter> leftList)
    {
        return leftList[^1].Name == "vararg";
    }
}
