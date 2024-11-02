using GSCode.Data;
using GSCode.Parser.AST.Expressions;
using GSCode.Parser.Data;
using GSCode.Parser.DFA;
using GSCode.Parser.SPA.Logic.Components;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.SPA.Logic.Analysers;

//internal class FunctionSignatureAnalyser
//{
//    public static List<ScrParameter>? Analyse(Expression expression, ParserIntelliSense sense)
//    {
//        // No data if failed
//        if(expression.Failed)
//        {
//            return null;
//        }

//        // If empty
//        if(expression.Empty)
//        {
//            return [];
//        }

//        return AnalyseNode(expression.Root!, sense);
//    }

//    public static List<ScrParameter>? AnalyseNode(IExpressionNode node, ParserIntelliSense sense)
//    {
//        return node.NodeType switch
//        {
//            ExpressionNodeType.Unknown => null,
//            // Not valid in arg list
//            ExpressionNodeType.Literal => AnalyseLiteral((TokenNode)node, sense),
//            ExpressionNodeType.Enclosure => IssueBadParameterError(node, sense),
//            // Possibly valid
//            ExpressionNodeType.Field => AnalyseField((TokenNode)node, sense),
//            ExpressionNodeType.Operation => AnalyseOperation((OperationNode)node, sense),
//            _ => throw new InvalidOperationException($"Unsupported node type: {node.NodeType}"),
//        };
//    }

//    public static List<ScrParameter>? AnalyseOperation(OperationNode node, ParserIntelliSense sense)
//    {
//        if(node.Operation != OperatorOps.Comma &&
//            node.Operation != OperatorOps.Assign &&
//            node.Operation != OperatorOps.AddressOf)
//        {
//            return IssueBadParameterError(node, sense);
//        }

//        // Base case when has a default value.
//        if(node.Operation == OperatorOps.Assign)
//        {
//            ScrParameter? result = AnalyseParameterWithDefault(node, sense);

//            if (result is ScrParameter param)
//            {
//                return
//                [
//                    param
//                ];
//            }
//        }
//        // Another base case is a passed by reference value.
//        else if(node.Operation == OperatorOps.AddressOf &&
//            node.Right is TokenNode node1)
//        {
//            ScrParameter? result = AnalyseParameter(node1, sense);
//            if (result is ScrParameter param)
//            {
//                return
//                [
//                    param
//                ];
//            }
//        }
//        // TODO: This is currently O(n), could be better by using a LL
//        else if(node.Operation == OperatorOps.Comma)
//        {
//            List<ScrParameter>? leftResult = AnalyseNode(node.Left!, sense);
//            List<ScrParameter>? rightResult = AnalyseNode(node.Right!, sense);

//            if(leftResult != null && rightResult != null)
//            {
//                // Edge case: Error if vararg (...) is not the very last parameter. Simultaneously doesn't allow for multiple varargs to be put in.
//                if(HasVararg(leftResult))
//                {
//                    sense.AddSpaDiagnostic(leftResult[leftResult.Count - 1].Range, GSCErrorCodes.VarargNotLastParameter);
//                    return null;
//                }

//                leftResult.AddRange(rightResult);
//                return leftResult;
//            }
//        }

//        return null;
//    }

//    public static List<ScrParameter>? AnalyseLiteral(TokenNode node, ParserIntelliSense sense)
//    {
//        if(node.SourceToken.Is(TokenType.Keyword, KeywordTypes.Vararg))
//        {
//            return new()
//            {
//                new ScrParameter("vararg", new ScrData(ScrDataTypes.Array), node.SourceToken.TextRange)
//            };
//        }

//        return IssueBadParameterError(node, sense);
//    }

//    public static List<ScrParameter>? AnalyseField(TokenNode node, ParserIntelliSense sense)
//    {
//        ScrParameter? result = AnalyseParameter(node, sense);

//        if (result is ScrParameter param)
//        {
//            return
//            [
//                param
//            ];
//        }
//        return null;
//    }

//    public static ScrParameter? AnalyseParameterWithDefault(OperationNode node, ParserIntelliSense sense)
//    {
//        // Check LHS has been given as a simple parameter name
//        if(node.Left is not TokenNode tokenNode ||
//            tokenNode.NodeType != ExpressionNodeType.Field)
//        {
//            sense.AddSpaDiagnostic(node.Range, GSCErrorCodes.IdentifierExpected);
//            return null;
//        }

//        // Don't allow the user to use vararg as a parameter name
//        if(tokenNode.SourceToken.Contents == "vararg")
//        {
//            sense.AddSpaDiagnostic(node.Range, GSCErrorCodes.ParameterNameReserved, "vararg");
//            return null;
//        }

//        // We don't evaluate defaults here, but do record where they are. TODO: this might change, as it should be a compile-time constant. Could use VOID to proxy for this.
//        return new ScrParameter(tokenNode.SourceToken.Contents, new ScrData(ScrDataTypes.Any), tokenNode.SourceToken.TextRange, node.Right);
//    }

//    public static ScrParameter? AnalyseParameter(TokenNode node, ParserIntelliSense sense)
//    {
//        // Check has been given as a simple parameter name
//        if (node.NodeType != ExpressionNodeType.Field)
//        {
//            sense.AddSpaDiagnostic(node.Range, GSCErrorCodes.IdentifierExpected);
//            return null;
//        }

//        // Don't allow the user to use vararg as a parameter name
//        if (node.SourceToken.Contents == "vararg")
//        {
//            sense.AddSpaDiagnostic(node.Range, GSCErrorCodes.ParameterNameReserved, "vararg");
//            return null;
//        }

//        return new ScrParameter(node.SourceToken.Contents, new ScrData(ScrDataTypes.Any), node.SourceToken.TextRange);
//    }

//    private static List<ScrParameter>? IssueBadParameterError(IExpressionNode node, ParserIntelliSense sense)
//    {
//        sense.AddSpaDiagnostic(node.Range, GSCErrorCodes.IdentifierExpected);
//        return null;
//    }

//    private static bool HasVararg(List<ScrParameter> leftList)
//    {
//        return leftList[^1].Name == "vararg";
//    }
//}
