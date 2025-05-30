﻿//using GSCode.Data;
//using GSCode.Data.Models;
//using GSCode.Parser.AST.Expressions;
//using GSCode.Parser.AST.Nodes;
//using GSCode.Parser.Data;
//using GSCode.Parser.DFA;
//using GSCode.Parser.SPA.Logic.Analysers;
//using GSCode.Parser.SPA.Logic.Components;
//using GSCode.Parser.Util;
//using Microsoft.VisualStudio.LanguageServer.Protocol;
//using System.Collections.Generic;
//using System.Runtime.Intrinsics.X86;

//namespace GSCode.Parser.SPA.Logic.Analysers;

//#if DEBUG

//internal class FileAnalyser : DataFlowNodeAnalyser
//{
//    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
//    {
//        // Implementation
//    }
//}

//internal class ConstDeclarationAnalyser : DataFlowNodeAnalyser
//{
//    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
//    {
//        // Extract the expression component
//        ExpressionComponent expression = (ExpressionComponent)currentNode.Components[1];

//        // Check the first node of the expression
//        IExpressionNode? firstNode = expression.Expression?.Root;

//        if (firstNode is OperationNode operationNode && operationNode.Operation == OperatorOps.Assign)
//        {
//            // Get the symbol name from the LHS of the assignment
//            if (operationNode.Left is TokenNode tokenNode && tokenNode.NodeType == ExpressionNodeType.Field)
//            {
//                string symbolName = tokenNode.SourceToken.Contents;

//                // Check for redefinition
//                if (symbolTable.ContainsSymbol(symbolName))
//                {
//                    sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(tokenNode.Range, DiagnosticSources.Spa, GSCErrorCodes.RedefinitionOfSymbol, symbolName));
//                    return;
//                }

//                // Otherwise, analyse the RHS, and use the result to assign a constant with value of the RHS
//                // Analyze the expression, which will evaluate & add the symbol to the symbol table.
//                ScrData value = ExpressionAnalyzer.AnalyseNode(operationNode.Right!, symbolTable, sense);
//                value.ReadOnly = true;

//                symbolTable.AddOrSetSymbol(symbolName, value);
//                sense.AddSenseToken(ScrVariableSymbol.Declaration(tokenNode, value, true));

//                return;
//            }

//            // ERROR: Variable declaration expected.
//            sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(operationNode.Left!.Range, DiagnosticSources.Spa, GSCErrorCodes.VariableDeclarationExpected));
//        }
//        else if (firstNode is not null)
//        {
//            // ERROR: The expression following a constant declaration must be an assignment.
//            sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(firstNode.Range, DiagnosticSources.Spa, GSCErrorCodes.InvalidExpressionFollowingConstDeclaration));
//        }
//    }
//}

//internal class ClassConstructorAnalyser : DataFlowNodeAnalyser
//{
//    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense) 
//    {
//        // TODO: Might need to unique check the constructor, not sure if gsc allows overloads.
//    }
//}

//internal class ClassDestructorAnalyser : DataFlowNodeAnalyser
//{
//    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
//    {
//        // TODO: Might need to unique check the destructor, not sure if gsc allows overloads.
//    }
//}

//internal class ClassDeclarationAnalyser : SignatureNodeAnalyser
//{
//    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, DefinitionsTable definitionsTable, ParserIntelliSense sense)
//    {
//        // Implementation for ClassDeclaration
//    }
//}

//internal class CaseStatementAnalyser : DataFlowNodeAnalyser
//{
//    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
//    {
//        // Implementation for CaseStatement
//    }
//}

//internal class DefaultStatementAnalyser : DataFlowNodeAnalyser
//{
//    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
//    {
//        // Implementation for DefaultStatement
//    }
//}

//internal class IfStatementAnalyser : DataFlowNodeAnalyser
//{
//    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
//    {
//        // Implementation for IfStatement

//        // Extract the expression component
//        ExpressionComponent expression = (ExpressionComponent)currentNode.Components[2];

//        // Analyse the expression, if it's not 'unknown' & can't be resolved to a bool, we error
//        ScrData result = ExpressionAnalyzer.Analyse(expression.Expression!, symbolTable, sense);

//        // Empty expression - which isn't valid for if
//        if(result.IsVoid())
//        {
//            sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(currentNode.TextRange, DiagnosticSources.Spa, GSCErrorCodes.ExpressionExpected));
//            return;
//        }

//        // Check that it can resolve to a bool
//        if(!result.TypeUnknown() && !result.CanEvaluateToBoolean())
//        {
//            sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(expression.Expression.Range, DiagnosticSources.Spa, GSCErrorCodes.NoImplicitConversionExists, result.TypeToString(), "bool"));
//        }

//        // Check if it came out to a constant value
//        if(result.CanEvaluateToBoolean())
//        {
//            bool? truthyValue = result.IsTruthy();
//            if(truthyValue is not bool truthy)
//            {
//                return;
//            }

//            if (truthy)
//            {
//                // handle always true
//                return;
//            }

//            // handle always false
//            // TODO: Disabled for now, as it's not working properly
//            //ASTBranch branch = currentNode.Branch!;

//            //if(branch.ChildrenCount > 0)
//            //{
//            //    Position start = branch.GetChild(0).TextRange.Start;
//            //    Position end = branch.GetLastChild().TextRange.End;

//            //    // WARNING: Unreachable code detected
//            //    sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(new Range
//            //    {
//            //        Start = start,
//            //        End = end
//            //    }, DiagnosticSources.SPA, GSCErrorCodes.UnreachableCodeDetected));
//            //}
//        }
//    }
//}

//internal class ElseIfStatementAnalyser : DataFlowNodeAnalyser
//{
//    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
//    {
//        // Check that this has been preceded by an else-if or an if.
//        if(previousNode is ASTNode)
//        {
//            if(previousNode.Type != NodeTypes.ElseIfStatement &&
//                previousNode.Type != NodeTypes.IfStatement)
//            {
//                sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(currentNode.StartToken.TextRange, DiagnosticSources.Spa, GSCErrorCodes.MissingAccompanyingConditional));
//            }
//        }

//        // Extract the expression component
//        ExpressionComponent expression = (ExpressionComponent)currentNode.Components[3];

//        // Analyse the expression, if it's not 'unknown' & can't be resolved to a bool, we error
//        ScrData result = ExpressionAnalyzer.Analyse(expression.Expression!, symbolTable, sense);

//        // Empty expression - which isn't valid for if
//        if (result.IsVoid())
//        {
//            sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(currentNode.TextRange, DiagnosticSources.Spa, GSCErrorCodes.ExpressionExpected));
//            return;
//        }

//        // Check that it can resolve to a bool
//        if (!result.TypeUnknown() && !result.CanEvaluateToBoolean())
//        {
//            sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(expression.Expression.Range, DiagnosticSources.Spa, GSCErrorCodes.NoImplicitConversionExists, result.TypeToString(), "bool"));
//        }

//        // Check if it came out to a constant value
//        if (result.CanEvaluateToBoolean())
//        {
//            bool? truthyValue = result.IsTruthy();
//            if (truthyValue is not bool truthy)
//            {
//                return;
//            }

//            if (truthy)
//            {
//                // handle always true
//                return;
//            }

//            // handle always false
//            ASTBranch branch = currentNode.Branch!;

//            if (branch.ChildrenCount > 0)
//            {
//                Position start = branch.GetChild(0).TextRange.Start;
//                Position end = branch.GetLastChild().TextRange.End;

//                // WARNING: Unreachable code detected
//                sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(new Range
//                {
//                    Start = start,
//                    End = end
//                }, DiagnosticSources.Spa, GSCErrorCodes.UnreachableCodeDetected));
//            }
//        }
//    }
//}

//internal class ElseStatementAnalyser : DataFlowNodeAnalyser
//{
//    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
//    {
//        // Check that this has been preceded by an else-if or an if.
//        if (previousNode is ASTNode)
//        {
//            if (previousNode.Type != NodeTypes.ElseIfStatement &&
//                previousNode.Type != NodeTypes.IfStatement)
//            {
//                sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(currentNode.StartToken.TextRange, DiagnosticSources.Spa, GSCErrorCodes.MissingAccompanyingConditional));
//            }
//        }
//    }
//}


//internal class FunctionDeclarationStaticAnalyser : DataFlowNodeAnalyser
//{
//    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
//    {

//        throw new NotImplementedException();
//    }
//}

//internal class WhileLoopAnalyser : DataFlowNodeAnalyser
//{
//    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
//    {
//        // Implementation for WhileLoop

//        // Check that this has been preceded by an 'do' if it has no body.
//        if (currentNode.Branch is null && 
//            (previousNode is not ASTNode ||
//            previousNode.Type != NodeTypes.DoLoop))
//        {
//            sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(currentNode.StartToken.TextRange, DiagnosticSources.Spa, GSCErrorCodes.MissingDoLoop));
//        }

//        // Extract the expression component
//        ExpressionComponent expression = (ExpressionComponent)currentNode.Components[2];

//        // Analyse the expression, if it's not 'unknown' & can't be resolved to a bool, we error
//        ScrData result = ExpressionAnalyzer.Analyse(expression.Expression!, symbolTable, sense);

//        // Empty expression - which isn't valid for if
//        if (result.IsVoid())
//        {
//            sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(currentNode.TextRange, DiagnosticSources.Spa, GSCErrorCodes.ExpressionExpected));
//            return;
//        }

//        // Check that it can resolve to a bool
//        if (!result.TypeUnknown() && !result.CanEvaluateToBoolean())
//        {
//            sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(expression.Expression.Range, DiagnosticSources.Spa, GSCErrorCodes.NoImplicitConversionExists, result.TypeToString(), "bool"));
//        }
//    }
//}

////internal class DoLoopAnalyser : NodeAnalyser
////{
////    public override NodeTypes NodeType => NodeTypes.DoLoop;

////    public override void Analyze(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
////    {

////    }
////}

//internal class PrecacheDirectiveAnalyser : DataFlowNodeAnalyser
//{
//    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
//    {
//        // Implementation for PrecacheDirective
//    }
//}

//internal class UsingAnimTreeDirectiveAnalyser : DataFlowNodeAnalyser
//{
//    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
//    {
//        // Implementation for UsingAnimTreeDirective
//    }
//}

//internal class ReturnStatementAnalyser : DataFlowNodeAnalyser
//{
//    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
//    {
//        // Implementation for ReturnStatement

//        // Extract the expression component
//        ExpressionComponent? expression = (ExpressionComponent?)currentNode.Components.FirstOrDefault(component => component as ExpressionComponent != null);

//        if(expression == null)
//        {
//            return;
//        }

//        // Analyze the expression
//        ScrData value = ExpressionAnalyzer.Analyse(expression.Expression, symbolTable, sense);

//        // something like 'Cannot return 'void'', but whatever it is it needs to be applicable to if, etc. too.
//        // TODO: this might chagne. If we decide not to make () retyrn void, bu7t error instead about its contents.
//        // but that might break function calls.

//        // TODO: Why does this need to be here? We don't have to return anything
//        //if (value.Type == ScrDataTypes.Void)
//        //{
//        //    sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(expression.Expression.Range, DiagnosticSources.SPA, GSCErrorCodes.ExpressionExpected));
//        //}
//    }
//}

//internal class WaitStatementAnalyser : DataFlowNodeAnalyser
//{
//    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
//    {
//        // Implementation for WaitStatement

//        // Extract the expression component
//        ExpressionComponent expression = (ExpressionComponent)currentNode.Components.First(component => component as ExpressionComponent != null);

//        // Analyze the expression
//        ScrData value = ExpressionAnalyzer.Analyse(expression.Expression, symbolTable, sense);

//        if(!value.IsOfTypes(ScrDataTypes.Int, ScrDataTypes.Float))
//        {
//            sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(expression.Expression.Range, DiagnosticSources.Spa, GSCErrorCodes.NoImplicitConversionExists, value.TypeToString(), "int | float"));
//            return;
//        }

//        double? numericValue = value.GetNumericValue();

//        if (numericValue is double number)
//        {
//            // Check if the value is less than or equal to 0
//            if (number <= 0)
//            {
//                sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(expression.Expression.Range, DiagnosticSources.Spa, GSCErrorCodes.CannotWaitNegativeDuration));
//                return;
//            }

//            // Scale up the number
//            double scaledNumber = number * 20;

//            // Round the scaled number
//            double roundedScaledNumber = Math.Round(scaledNumber);

//            // If the difference is 0, then 'number' is a multiple of 0.05
//            if (roundedScaledNumber != scaledNumber)
//            {
//                // The number is not a multiple of 0.05, so round up to the next multiple.
//                double rounded = Math.Ceiling(number / 0.05) * 0.05;

//                // Add the diagnostic message
//                sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(expression.Expression.Range, DiagnosticSources.Spa, GSCErrorCodes.BelowVmRefreshRate, "GSC", "20", number, rounded));
//            }
//        }
//    }
//}

//internal class WaitRealTimeStatementAnalyser : DataFlowNodeAnalyser
//{
//    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
//    {
//        // Implementation for WaitRealTimeStatement
//    }
//}

//internal class SwitchStatementAnalyser : DataFlowNodeAnalyser
//{
//    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
//    {
//        // Implementation for SwitchStatement
//    }
//}

//internal class ExpressionStatementAnalyser : DataFlowNodeAnalyser
//{
//    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
//    {
//        // Extract the expression component
//        ExpressionComponent expression = (ExpressionComponent)currentNode.Components[0];

//        // Analyze the expression
//        ScrData value = ExpressionAnalyzer.Analyse(expression.Expression, symbolTable, sense);

//        // Check the first node of the expression
//        IExpressionNode? firstNode = expression.Expression?.Root;

//        if(firstNode is not OperationNode operationNode ||
//            (operationNode.Operation != OperatorOps.AssignBitLeftShift &&
//            operationNode.Operation != OperatorOps.AssignBitRightShift &&
//            operationNode.Operation != OperatorOps.AssignBitOr &&
//            operationNode.Operation != OperatorOps.AssignBitAnd &&
//            operationNode.Operation != OperatorOps.AssignBitXor &&
//            operationNode.Operation != OperatorOps.AssignDivide &&
//            operationNode.Operation != OperatorOps.AssignMultiply &&
//            operationNode.Operation != OperatorOps.AssignPlus &&
//            operationNode.Operation != OperatorOps.AssignRemainder &&
//            operationNode.Operation != OperatorOps.AssignSubtract &&
//            operationNode.Operation != OperatorOps.Assign &&
//            operationNode.Operation != OperatorOps.PreIncrement &&
//            operationNode.Operation != OperatorOps.PostIncrement &&
//            operationNode.Operation != OperatorOps.PreDecrement &&
//            operationNode.Operation != OperatorOps.PostDecrement &&
//            operationNode.Operation != OperatorOps.ThreadedFunctionCall &&
//            operationNode.Operation != OperatorOps.FunctionCall &&
//            operationNode.Operation != OperatorOps.NewObject &&
//            operationNode.Operation != OperatorOps.CalledOnEntity))
//        {
//            sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(firstNode!.Range, DiagnosticSources.Spa, GSCErrorCodes.InvalidExpressionStatement));
//        }
//    }
//}

//internal class ForeachLoopAnalyser : DataFlowNodeAnalyser
//{
//    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
//    {
//        // Implementation for ForeachLoop
//        // TODO: if source isn't Unknown, check that it's an array
//    }
//}

//internal class ForLoopAnalyser : DataFlowNodeAnalyser
//{
//    public override void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense)
//    {
//        // Implementation for ForLoop
//    }
//}

//#endif