using GSCode.Data;
using GSCode.Data.Models;
using GSCode.Parser.AST.Expressions;
using GSCode.Parser.AST.Nodes;
using GSCode.Parser.Data;
using GSCode.Parser.DFA;
using GSCode.Parser.SPA.Logic.Analysers;
using GSCode.Parser.SPA.Logic.Components;
using GSCode.Parser.Util;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using System.Collections.Generic;
using System.Runtime.Intrinsics.X86;

namespace GSCode.Parser.SPA.Logic.Analysers;

#if DEBUG

internal class FileAnalyser : DataFlowNodeAnalyser
{
    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        // Implementation
    }
}

internal class ConstDeclarationAnalyser : DataFlowNodeAnalyser
{
    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        // Extract the expression component
        ExpressionComponent expression = (ExpressionComponent)currentNode.Components[1];

        // Check the first node of the expression
        IExpressionNode? firstNode = expression.Expression?.Root;

        if (firstNode is OperationNode operationNode && operationNode.Operation == OperatorOps.Assign)
        {
            // Get the symbol name from the LHS of the assignment
            if (operationNode.Left is TokenNode tokenNode && tokenNode.NodeType == ExpressionNodeType.Field)
            {
                string symbolName = tokenNode.SourceToken.Contents;

                // Check for redefinition
                if (symbolTable.ContainsSymbol(symbolName))
                {
                    sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(tokenNode.Range, DiagnosticSources.SPA, GSCErrorCodes.RedefinitionOfSymbol, symbolName));
                    return;
                }

                // Otherwise, analyse the RHS, and use the result to assign a constant with value of the RHS
                // Analyze the expression, which will evaluate & add the symbol to the symbol table.
                ScrData value = ExpressionAnalyzer.AnalyseNode(operationNode.Right!, symbolTable, sense);
                value.ReadOnly = true;

                symbolTable.AddOrSetSymbol(symbolName, value);
                sense.AddSenseToken(ScrVariableSymbol.Declaration(tokenNode, value, true));

                return;
            }

            // ERROR: Variable declaration expected.
            sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(operationNode.Left!.Range, DiagnosticSources.SPA, GSCErrorCodes.VariableDeclarationExpected));
        }
        else if (firstNode is not null)
        {
            // ERROR: The expression following a constant declaration must be an assignment.
            sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(firstNode.Range, DiagnosticSources.SPA, GSCErrorCodes.InvalidExpressionFollowingConstDeclaration));
        }
    }
}

internal class ClassConstructorAnalyser : DataFlowNodeAnalyser
{
    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense) 
    {
        // TODO: Might need to unique check the constructor, not sure if gsc allows overloads.
    }
}

internal class ClassDestructorAnalyser : DataFlowNodeAnalyser
{
    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        // TODO: Might need to unique check the destructor, not sure if gsc allows overloads.
    }
}

internal class ClassDeclarationAnalyser : SignatureNodeAnalyser
{
    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, DefinitionsTable definitionsTable, ParserIntelliSense sense)
    {
        // Implementation for ClassDeclaration
    }
}

internal class CaseStatementAnalyser : DataFlowNodeAnalyser
{
    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        // Implementation for CaseStatement
    }
}

internal class DefaultStatementAnalyser : DataFlowNodeAnalyser
{
    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        // Implementation for DefaultStatement
    }
}

internal class UsingDirectiveAnalyser : SignatureNodeAnalyser
{
    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, DefinitionsTable definitionsTable, ParserIntelliSense sense)
    {
        // Implementation for UsingDirective
        FilePathComponent filePathComponent = (FilePathComponent)currentNode.Components[1];

        // Failed
        if(filePathComponent.ScriptPath is null)
        {
            return;
        }

        definitionsTable.AddDependency(filePathComponent.ScriptPath);
    }
}

internal class IfStatementAnalyser : DataFlowNodeAnalyser
{
    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        // Implementation for IfStatement

        // Extract the expression component
        ExpressionComponent expression = (ExpressionComponent)currentNode.Components[2];

        // Analyse the expression, if it's not 'unknown' & can't be resolved to a bool, we error
        ScrData result = ExpressionAnalyzer.Analyse(expression.Expression!, symbolTable, sense);

        // Empty expression - which isn't valid for if
        if(result.IsVoid())
        {
            sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(currentNode.TextRange, DiagnosticSources.SPA, GSCErrorCodes.ExpressionExpected));
            return;
        }

        // Check that it can resolve to a bool
        if(!result.TypeUnknown() && !result.CanEvaluateToBoolean())
        {
            sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(expression.Expression.Range, DiagnosticSources.SPA, GSCErrorCodes.NoImplicitConversionExists, result.TypeToString(), "bool"));
        }

        // Check if it came out to a constant value
        if(result.CanEvaluateToBoolean())
        {
            bool? truthyValue = result.IsTruthy();
            if(truthyValue is not bool truthy)
            {
                return;
            }

            if (truthy)
            {
                // handle always true
                return;
            }

            // handle always false
            // TODO: Disabled for now, as it's not working properly
            //ASTBranch branch = currentNode.Branch!;

            //if(branch.ChildrenCount > 0)
            //{
            //    Position start = branch.GetChild(0).TextRange.Start;
            //    Position end = branch.GetLastChild().TextRange.End;

            //    // WARNING: Unreachable code detected
            //    sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(new Range
            //    {
            //        Start = start,
            //        End = end
            //    }, DiagnosticSources.SPA, GSCErrorCodes.UnreachableCodeDetected));
            //}
        }
    }
}

internal class ElseIfStatementAnalyser : DataFlowNodeAnalyser
{
    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        // Check that this has been preceded by an else-if or an if.
        if(previousNode is ASTNode)
        {
            if(previousNode.Type != NodeTypes.ElseIfStatement &&
                previousNode.Type != NodeTypes.IfStatement)
            {
                sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(currentNode.StartToken.TextRange, DiagnosticSources.SPA, GSCErrorCodes.MissingAccompanyingConditional));
            }
        }

        // Extract the expression component
        ExpressionComponent expression = (ExpressionComponent)currentNode.Components[3];

        // Analyse the expression, if it's not 'unknown' & can't be resolved to a bool, we error
        ScrData result = ExpressionAnalyzer.Analyse(expression.Expression!, symbolTable, sense);

        // Empty expression - which isn't valid for if
        if (result.IsVoid())
        {
            sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(currentNode.TextRange, DiagnosticSources.SPA, GSCErrorCodes.ExpressionExpected));
            return;
        }

        // Check that it can resolve to a bool
        if (!result.TypeUnknown() && !result.CanEvaluateToBoolean())
        {
            sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(expression.Expression.Range, DiagnosticSources.SPA, GSCErrorCodes.NoImplicitConversionExists, result.TypeToString(), "bool"));
        }

        // Check if it came out to a constant value
        if (result.CanEvaluateToBoolean())
        {
            bool? truthyValue = result.IsTruthy();
            if (truthyValue is not bool truthy)
            {
                return;
            }

            if (truthy)
            {
                // handle always true
                return;
            }

            // handle always false
            ASTBranch branch = currentNode.Branch!;

            if (branch.ChildrenCount > 0)
            {
                Position start = branch.GetChild(0).TextRange.Start;
                Position end = branch.GetLastChild().TextRange.End;

                // WARNING: Unreachable code detected
                sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(new Range
                {
                    Start = start,
                    End = end
                }, DiagnosticSources.SPA, GSCErrorCodes.UnreachableCodeDetected));
            }
        }
    }
}

internal class ElseStatementAnalyser : DataFlowNodeAnalyser
{
    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        // Check that this has been preceded by an else-if or an if.
        if (previousNode is ASTNode)
        {
            if (previousNode.Type != NodeTypes.ElseIfStatement &&
                previousNode.Type != NodeTypes.IfStatement)
            {
                sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(currentNode.StartToken.TextRange, DiagnosticSources.SPA, GSCErrorCodes.MissingAccompanyingConditional));
            }
        }
    }
}

internal class FunctionDeclarationSignatureAnalyser : SignatureNodeAnalyser
{
    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, DefinitionsTable definitionsTable, ParserIntelliSense sense)
    {
        // Extract the expression component
        NameComponent functionName = (NameComponent)currentNode.Components.First(component => component is NameComponent nameComponent && nameComponent.Type == TokenType.Name);
        ExpressionComponent expression = (ExpressionComponent)currentNode.Components.First(component => component as ExpressionComponent != null);

        // Analyze the parameter list
        List<ScrParameter>? parameters = FunctionSignatureAnalyser.Analyse(expression.Expression, sense);

        // Produce a definition for our function
        definitionsTable.AddFunction(new ScrFunction()
        {
            Name = functionName.GetSymbolName(),
            Description = null, // TODO: Check the DOC COMMENT
            Args = GetParametersAsRecord(parameters),
            CalledOn = new ScrFunctionArg()
            {
                Name = "unk",
                Required = false
            }, // TODO: Check the DOC COMMENT
            Returns = new ScrFunctionArg()
            {
                Name = "unk",
                Required = false
            }, // TODO: Check the DOC COMMENT
            Tags = new() { "userdefined" },
            IntelliSense = null // I have no idea why this exists
        }, currentNode.Branch);

        if(parameters is not null)
        {
            foreach(ScrParameter parameter in parameters)
            {
                sense.AddSenseToken(new ScrParameterSymbol(parameter));
            }
        }

        //if(expression.Expression.Empty || expression.Expression.Failed)
        //{
        //    return;
        //}

        //IExpressionNode node = expression.Expression.Root!;
        //if (value is not ScrArguments arguments)
        //{
        //    if((node is not TokenNode || node.NodeType != ExpressionNodeType.Field) &&
        //        (node is not OperationNode opNode || opNode.Operation == OperatorOps.Assign))
        //    {
        //        // ERROR: Expected a parameters list
        //        return;
        //    }

        //    AnalyzeArgument(node, symbolTable, sense);
        //    return;
        //}

        //// Check & define each parameter, add them to the symbol table
        //foreach(IExpressionNode argument in arguments.Arguments)
        //{
        //    AnalyzeArgument(argument, symbolTable, sense);
        //}
    }

    private static List<ScrFunctionArg>? GetParametersAsRecord(List<ScrParameter>? parameters)
    {
        if (parameters == null)
        {
            return null;
        }

        List<ScrFunctionArg> result = new();
        foreach(ScrParameter parameter in parameters)
        {
            result.Add(new ScrFunctionArg()
            {
                Name = parameter.Name,
                Description = null, // TODO: Check the DOC COMMENT
                Type = "unknown", // TODO: Check the DOC COMMENT
                Required = parameter.DefaultNode is null,
                Default = null // Not sure we can populate this
            });
        }

        return result;
    }

    //public override void AnalyzeForExport(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, DefinitionsTable definitionsTable, ParserIntelliSense sense)
    //{
    //    // Extract the expression component
    //    ExpressionComponent expression = (ExpressionComponent)currentNode.Components.First(component => component as ExpressionComponent != null);

    //    // Produce a ScrFunction
    //    NameComponent nameComponent = (NameComponent)currentNode.Components.First(c => c as NameComponent != null);

    //    ScrFunction scriptFunction = new()
    //    {
    //        Name = nameComponent.GetSymbolName(),
    //        Args = new()
    //    };

    //    if (expression.Expression.Empty || expression.Expression.Failed)
    //    {
    //        return;
    //    }

    //    // Analyze the expression
    //    ScrData value = ExpressionAnalyser.Analyse(expression.Expression, symbolTable, sense);

    //    IExpressionNode node = expression.Expression.Root!;
    //    if (value is not ScrArguments arguments)
    //    {
    //        // Check for error, which is suppressed on a dependency
    //        if ((node is not TokenNode || node.NodeType != ExpressionNodeType.Field) &&
    //            (node is not OperationNode opNode || opNode.Operation != OperatorOps.Assign))
    //        {
    //            return;
    //        }

    //        scriptFunction.Args.Add(new ScrFunctionArg
    //        {
    //            Name = node.GetFunctionDeclArgName()
    //        });
    //        return;
    //    }

    //    // Check & define each parameter, add them to the symbol table
    //    foreach (IExpressionNode argument in arguments.Arguments)
    //    {
    //        // Check for error, which is suppressed on a dependency
    //        if ((argument is not TokenNode || argument.NodeType != ExpressionNodeType.Field) &&
    //            (argument is not OperationNode opNode || opNode.Operation != OperatorOps.Assign))
    //        {
    //            return;
    //        }

    //        scriptFunction.Args.Add(new ScrFunctionArg
    //        {
    //            Name = argument.GetFunctionDeclArgName()
    //        });
    //    }
    //}

}

internal class FunctionDeclarationStaticAnalyser : DataFlowNodeAnalyser
{
    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
    {

        throw new NotImplementedException();
    }
}

internal class WhileLoopAnalyser : DataFlowNodeAnalyser
{
    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        // Implementation for WhileLoop

        // Check that this has been preceded by an 'do' if it has no body.
        if (currentNode.Branch is null && 
            (previousNode is not ASTNode ||
            previousNode.Type != NodeTypes.DoLoop))
        {
            sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(currentNode.StartToken.TextRange, DiagnosticSources.SPA, GSCErrorCodes.MissingDoLoop));
        }

        // Extract the expression component
        ExpressionComponent expression = (ExpressionComponent)currentNode.Components[2];

        // Analyse the expression, if it's not 'unknown' & can't be resolved to a bool, we error
        ScrData result = ExpressionAnalyzer.Analyse(expression.Expression!, symbolTable, sense);

        // Empty expression - which isn't valid for if
        if (result.IsVoid())
        {
            sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(currentNode.TextRange, DiagnosticSources.SPA, GSCErrorCodes.ExpressionExpected));
            return;
        }

        // Check that it can resolve to a bool
        if (!result.TypeUnknown() && !result.CanEvaluateToBoolean())
        {
            sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(expression.Expression.Range, DiagnosticSources.SPA, GSCErrorCodes.NoImplicitConversionExists, result.TypeToString(), "bool"));
        }
    }
}

//internal class DoLoopAnalyser : NodeAnalyser
//{
//    public override NodeTypes NodeType => NodeTypes.DoLoop;

//    public override void Analyze(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
//    {
        
//    }
//}

internal class PrecacheDirectiveAnalyser : DataFlowNodeAnalyser
{
    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        // Implementation for PrecacheDirective
    }
}

internal class NamespaceDirectiveAnalyser : SignatureNodeAnalyser
{
    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, DefinitionsTable definitionsTable, ParserIntelliSense sense)
    {
        // Extract the new namespace
        NameComponent newNamespace = (NameComponent)currentNode.Components.First(component => component is NameComponent nameComponent && nameComponent.Type == TokenType.Name);

        definitionsTable.CurrentNamespace = newNamespace.GetSymbolName();
    }
}

internal class UsingAnimTreeDirectiveAnalyser : DataFlowNodeAnalyser
{
    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        // Implementation for UsingAnimTreeDirective
    }
}

internal class ReturnStatementAnalyser : DataFlowNodeAnalyser
{
    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        // Implementation for ReturnStatement

        // Extract the expression component
        ExpressionComponent? expression = (ExpressionComponent?)currentNode.Components.FirstOrDefault(component => component as ExpressionComponent != null);

        if(expression == null)
        {
            return;
        }

        // Analyze the expression
        ScrData value = ExpressionAnalyzer.Analyse(expression.Expression, symbolTable, sense);

        // something like 'Cannot return 'void'', but whatever it is it needs to be applicable to if, etc. too.
        // TODO: this might chagne. If we decide not to make () retyrn void, bu7t error instead about its contents.
        // but that might break function calls.

        // TODO: Why does this need to be here? We don't have to return anything
        //if (value.Type == ScrDataTypes.Void)
        //{
        //    sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(expression.Expression.Range, DiagnosticSources.SPA, GSCErrorCodes.ExpressionExpected));
        //}
    }
}

internal class WaitStatementAnalyser : DataFlowNodeAnalyser
{
    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        // Implementation for WaitStatement

        // Extract the expression component
        ExpressionComponent expression = (ExpressionComponent)currentNode.Components.First(component => component as ExpressionComponent != null);

        // Analyze the expression
        ScrData value = ExpressionAnalyzer.Analyse(expression.Expression, symbolTable, sense);
        
        if(!value.IsOfTypes(ScrDataTypes.Int, ScrDataTypes.Float))
        {
            sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(expression.Expression.Range, DiagnosticSources.SPA, GSCErrorCodes.NoImplicitConversionExists, value.TypeToString(), "int | float"));
            return;
        }

        double? numericValue = value.GetNumericValue();

        if (numericValue is double number)
        {
            // Check if the value is less than or equal to 0
            if (number <= 0)
            {
                sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(expression.Expression.Range, DiagnosticSources.SPA, GSCErrorCodes.CannotWaitNegativeDuration));
                return;
            }

            // Scale up the number
            double scaledNumber = number * 20;

            // Round the scaled number
            double roundedScaledNumber = Math.Round(scaledNumber);

            // If the difference is 0, then 'number' is a multiple of 0.05
            if (roundedScaledNumber != scaledNumber)
            {
                // The number is not a multiple of 0.05, so round up to the next multiple.
                double rounded = Math.Ceiling(number / 0.05) * 0.05;

                // Add the diagnostic message
                sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(expression.Expression.Range, DiagnosticSources.SPA, GSCErrorCodes.BelowVmRefreshRate, "GSC", "20", number, rounded));
            }
        }
    }
}

internal class WaitRealTimeStatementAnalyser : DataFlowNodeAnalyser
{
    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        // Implementation for WaitRealTimeStatement
    }
}

internal class SwitchStatementAnalyser : DataFlowNodeAnalyser
{
    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        // Implementation for SwitchStatement
    }
}

internal class ExpressionStatementAnalyser : DataFlowNodeAnalyser
{
    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        // Extract the expression component
        ExpressionComponent expression = (ExpressionComponent)currentNode.Components[0];

        // Analyze the expression
        ScrData value = ExpressionAnalyzer.Analyse(expression.Expression, symbolTable, sense);

        // Check the first node of the expression
        IExpressionNode? firstNode = expression.Expression?.Root;

        if(firstNode is not OperationNode operationNode ||
            (operationNode.Operation != OperatorOps.AssignBitLeftShift &&
            operationNode.Operation != OperatorOps.AssignBitRightShift &&
            operationNode.Operation != OperatorOps.AssignBitOr &&
            operationNode.Operation != OperatorOps.AssignBitAnd &&
            operationNode.Operation != OperatorOps.AssignBitXor &&
            operationNode.Operation != OperatorOps.AssignDivide &&
            operationNode.Operation != OperatorOps.AssignMultiply &&
            operationNode.Operation != OperatorOps.AssignPlus &&
            operationNode.Operation != OperatorOps.AssignRemainder &&
            operationNode.Operation != OperatorOps.AssignSubtract &&
            operationNode.Operation != OperatorOps.Assign &&
            operationNode.Operation != OperatorOps.PreIncrement &&
            operationNode.Operation != OperatorOps.PostIncrement &&
            operationNode.Operation != OperatorOps.PreDecrement &&
            operationNode.Operation != OperatorOps.PostDecrement &&
            operationNode.Operation != OperatorOps.ThreadedFunctionCall &&
            operationNode.Operation != OperatorOps.FunctionCall &&
            operationNode.Operation != OperatorOps.NewObject &&
            operationNode.Operation != OperatorOps.CalledOnEntity))
        {
            sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(firstNode!.Range, DiagnosticSources.SPA, GSCErrorCodes.InvalidExpressionStatement));
        }
    }
}

internal class ForeachLoopAnalyser : DataFlowNodeAnalyser
{
    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        // Implementation for ForeachLoop
        // TODO: if source isn't Unknown, check that it's an array
    }
}

internal class ForLoopAnalyser : DataFlowNodeAnalyser
{
    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        // Implementation for ForLoop
    }
}

#endif