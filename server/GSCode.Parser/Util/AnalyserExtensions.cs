using GSCode.Parser.AST.Expressions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.Util;

#if PREVIEW
internal static class AnalyserExtensions
{
    public static string GetFunctionDeclArgName(this IExpressionNode expression)
    {
        // Simple arg name
        if(expression is TokenNode tokenNode &&
            tokenNode.NodeType == ExpressionNodeType.Field)
        {
            return tokenNode.SourceToken.Contents;
        }
        // Arg name is LHS of an assignment
        else if(expression is OperationNode operationNode &&
            operationNode.Operation == OperatorOps.Assign)
        {
            if(operationNode.Left is not TokenNode leftTokenNode ||
                leftTokenNode.NodeType != ExpressionNodeType.Field)
            {
                throw new Exception("BUG: ScrArguments is not sufficiently validating its input data, received a non-token on LHS of assign.");
            }
            return leftTokenNode.SourceToken.Contents;
        }
        throw new InvalidOperationException("The expression node passed does not fit the function declaration format.");
    }
}

#endif