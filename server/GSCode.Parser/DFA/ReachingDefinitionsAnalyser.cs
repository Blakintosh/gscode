using System.Numerics;
using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.CFA;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.SA;
using GSCode.Parser.SPA;
using GSCode.Parser.SPA.Logic.Components;
using Serilog;

namespace GSCode.Parser.DFA;

internal class SwitchAnalysisContext
{
    public ScrData SwitchExpressionType { get; set; } = ScrData.Default;
    public HashSet<string> SeenLabelValues { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<SwitchCaseDecisionNode> AnalyzedNodes { get; } = new();
    public bool HasDefault { get; set; } = false;
}

internal ref struct ReachingDefinitionsAnalyser(List<Tuple<ScrFunction, ControlFlowGraph>> functionGraphs, List<Tuple<ScrClass, ControlFlowGraph>> classGraphs, ParserIntelliSense sense, Dictionary<string, IExportedSymbol> exportedSymbolTable, ScriptAnalyserData? apiData = null)
{
    public List<Tuple<ScrFunction, ControlFlowGraph>> FunctionGraphs { get; } = functionGraphs;
    public List<Tuple<ScrClass, ControlFlowGraph>> ClassGraphs { get; } = classGraphs;
    public ParserIntelliSense Sense { get; } = sense;
    public Dictionary<string, IExportedSymbol> ExportedSymbolTable { get; } = exportedSymbolTable;
    public ScriptAnalyserData? ApiData { get; } = apiData;

    public Dictionary<CfgNode, Dictionary<string, ScrVariable>> InSets { get; } = new();
    public Dictionary<CfgNode, Dictionary<string, ScrVariable>> OutSets { get; } = new();

    public Dictionary<SwitchNode, SwitchAnalysisContext> SwitchContexts { get; } = new();
    private Dictionary<CfgNode, ScrClass> NodeToClassMap { get; } = new();

    public bool Silent { get; set; } = true;

    public void Run()
    {
        foreach (Tuple<ScrFunction, ControlFlowGraph> functionGraph in FunctionGraphs)
        {
            AnalyseFunction(functionGraph.Item1, functionGraph.Item2);
        }

        foreach (Tuple<ScrClass, ControlFlowGraph> classGraph in ClassGraphs)
        {
            AnalyseClass(classGraph.Item1, classGraph.Item2);
        }
    }

    public void AnalyseFunction(ScrFunction function, ControlFlowGraph functionGraph)
    {
        Silent = true;

        // Clear switch contexts at the start of each function analysis
        SwitchContexts.Clear();

        Stack<CfgNode> worklist = new();
        worklist.Push(functionGraph.Start);

        HashSet<CfgNode> visited = new();

        // Calculate iteration limit based on graph size to prevent infinite loops
        int totalNodes = CountAllNodes(functionGraph);
        int maxIterations = Math.Max(100, totalNodes * 5); // At least 100, or 5x nodes
        int iterations = 0;

        while (worklist.Count > 0 && iterations < maxIterations)
        {
            iterations++;
            CfgNode node = worklist.Pop();
            visited.Add(node);

            // Calculate the in set
            Dictionary<string, ScrVariable> inSet = new(StringComparer.OrdinalIgnoreCase);
            foreach (CfgNode incoming in node.Incoming)
            {
                if (OutSets.TryGetValue(incoming, out Dictionary<string, ScrVariable>? value))
                {
                    inSet.MergeTables(value, node.Scope);
                }
            }

            // Check if the in set has changed, if not, then we can skip this node.
            if (InSets.TryGetValue(node, out Dictionary<string, ScrVariable>? currentInSet) && currentInSet.VariableTableEquals(inSet))
            {
                continue;
            }

            // Update the in & out sets
            InSets[node] = inSet;

            // Store the previous outset for comparison
            Dictionary<string, ScrVariable>? previousOutSet = null;
            if (OutSets.TryGetValue(node, out Dictionary<string, ScrVariable>? existingOutSet))
            {
                // Create a copy of the existing outset for comparison
                previousOutSet = new Dictionary<string, ScrVariable>(existingOutSet, StringComparer.OrdinalIgnoreCase);
            }

            if (!OutSets.ContainsKey(node))
            {
                OutSets[node] = new Dictionary<string, ScrVariable>(StringComparer.OrdinalIgnoreCase);
            }

            // Calculate the out set
            if (node.Type == CfgNodeType.FunctionEntry)
            {
                AnalyseFunctionEntry((FunEntryBlock)node, inSet);
                OutSets[node] = inSet;
            }
            else if (node.Type == CfgNodeType.ClassEntry)
            {
                // Class entry - just pass through the in set
                OutSets[node] = inSet;
            }
            else if (node.Type == CfgNodeType.BasicBlock)
            {
                ScrClass? currentClass = NodeToClassMap.GetValueOrDefault(node);
                SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass);

                // TODO: Unioning of sets is not ideal, better to merge the ScrDatas of common key across multiple dictionaries. Easier to use with the symbol tables.
                // TODO: Analyse statement-by-statement, using the analysers already created, and get the out set.
                //Analyse(node, symbolTable, inSets, outSets, Sense);
                //outSet.UnionWith(symbolTable.GetOutgoingSymbols());
                AnalyseBasicBlock((BasicBlock)node, symbolTable);

                OutSets[node] = symbolTable.VariableSymbols;
            }
            else if (node.Type == CfgNodeType.ClassMembersBlock)
            {
                ScrClass? currentClass = NodeToClassMap.GetValueOrDefault(node);
                SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass);

                AnalyseClassMembersBlock((ClassMembersBlock)node, symbolTable);
                OutSets[node] = symbolTable.VariableSymbols;
            }
            else if (node.Type == CfgNodeType.EnumerationNode)
            {
                ScrClass? currentClass = NodeToClassMap.GetValueOrDefault(node);
                SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass);

                AnalyseEnumeration((EnumerationNode)node, symbolTable);
                OutSets[node] = symbolTable.VariableSymbols;
            }
            else if (node.Type == CfgNodeType.IterationNode)
            {
                ScrClass? currentClass = NodeToClassMap.GetValueOrDefault(node);
                SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass);

                AnalyseIteration((IterationNode)node, symbolTable);
                OutSets[node] = symbolTable.VariableSymbols;
            }
            else if (node.Type == CfgNodeType.DecisionNode)
            {
                ScrClass? currentClass = NodeToClassMap.GetValueOrDefault(node);
                SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass);

                AnalyseDecision((DecisionNode)node, symbolTable);
                OutSets[node] = symbolTable.VariableSymbols;
            }
            else if (node.Type == CfgNodeType.SwitchNode)
            {
                ScrClass? currentClass = NodeToClassMap.GetValueOrDefault(node);
                SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass);

                AnalyseSwitch((SwitchNode)node, symbolTable);
                OutSets[node] = symbolTable.VariableSymbols;
            }
            else if (node.Type == CfgNodeType.SwitchCaseDecisionNode)
            {
                ScrClass? currentClass = NodeToClassMap.GetValueOrDefault(node);
                SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass);

                AnalyseSwitchCaseDecision((SwitchCaseDecisionNode)node, symbolTable);
                OutSets[node] = symbolTable.VariableSymbols;
            }
            else
            {
                // TODO: temp - just copy the in set to the out set.
                OutSets[node] = inSet;
            }

            // Check if the outset has changed before queueing successors.
            bool outSetChanged = previousOutSet == null || !previousOutSet.VariableTableEquals(OutSets[node]);

            // Only add successors to the worklist if the outset has changed
            if (!outSetChanged)
            {
                continue;
            }

            foreach (CfgNode successor in node.Outgoing)
            {
                worklist.Push(successor);
            }
        }

        // Check if we hit the iteration limit
        if (iterations >= maxIterations)
        {
            Log.Warning("Reaching definitions analysis hit iteration limit ({maxIterations}) for function {functionName}. This may indicate convergence issues.",
                maxIterations, function.Name ?? "<anonymous>");
        }

        // Now that analysis is done, do one final pass to add diagnostics.
        Silent = false;

        foreach (CfgNode node in visited)
        {
            if (!InSets.TryGetValue(node, out Dictionary<string, ScrVariable>? inSet))
            {
                continue;
            }

            // Re-run analysis with Silent = false to generate diagnostics
            ScrClass? currentClass = NodeToClassMap.GetValueOrDefault(node);
            switch (node.Type)
            {
                case CfgNodeType.BasicBlock:
                    AnalyseBasicBlock((BasicBlock)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass));
                    break;
                case CfgNodeType.ClassMembersBlock:
                    AnalyseClassMembersBlock((ClassMembersBlock)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass));
                    break;
                case CfgNodeType.EnumerationNode:
                    AnalyseEnumeration((EnumerationNode)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass));
                    break;
                case CfgNodeType.IterationNode:
                    AnalyseIteration((IterationNode)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass));
                    break;
                case CfgNodeType.SwitchNode:
                    AnalyseSwitch((SwitchNode)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass));
                    break;
                case CfgNodeType.SwitchCaseDecisionNode:
                    AnalyseSwitchCaseDecision((SwitchCaseDecisionNode)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass));
                    break;
                case CfgNodeType.DecisionNode:
                    AnalyseDecision((DecisionNode)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass));
                    break;
            }
        }
    }

    public void AnalyseClass(ScrClass scrClass, ControlFlowGraph classGraph)
    {
        Silent = true;

        // Clear switch contexts at the start of each class analysis
        SwitchContexts.Clear();

        // Build a map of all function entry nodes to this class
        BuildClassContextMap(classGraph.Start, scrClass);

        Stack<CfgNode> worklist = new();
        worklist.Push(classGraph.Start);

        HashSet<CfgNode> visited = new();

        // Calculate iteration limit based on graph size to prevent infinite loops
        int totalNodes = CountAllNodes(classGraph);
        int maxIterations = Math.Max(100, totalNodes * 5); // At least 100, or 5x nodes
        int iterations = 0;

        while (worklist.Count > 0 && iterations < maxIterations)
        {
            iterations++;
            CfgNode node = worklist.Pop();
            visited.Add(node);

            // Calculate the in set
            Dictionary<string, ScrVariable> inSet = new(StringComparer.OrdinalIgnoreCase);
            foreach (CfgNode incoming in node.Incoming)
            {
                if (OutSets.TryGetValue(incoming, out Dictionary<string, ScrVariable>? value))
                {
                    inSet.MergeTables(value, node.Scope);
                }
            }

            // Check if the in set has changed, if not, then we can skip this node.
            if (InSets.TryGetValue(node, out Dictionary<string, ScrVariable>? currentInSet) && currentInSet.VariableTableEquals(inSet))
            {
                continue;
            }

            // Update the in & out sets
            InSets[node] = inSet;

            // Store the previous outset for comparison
            Dictionary<string, ScrVariable>? previousOutSet = null;
            if (OutSets.TryGetValue(node, out Dictionary<string, ScrVariable>? existingOutSet))
            {
                // Create a copy of the existing outset for comparison
                previousOutSet = new Dictionary<string, ScrVariable>(existingOutSet, StringComparer.OrdinalIgnoreCase);
            }

            if (!OutSets.ContainsKey(node))
            {
                OutSets[node] = new Dictionary<string, ScrVariable>(StringComparer.OrdinalIgnoreCase);
            }

            // Calculate the out set
            if (node.Type == CfgNodeType.ClassEntry)
            {
                // Class entry - just pass through the in set
                OutSets[node] = inSet;
            }
            else if (node.Type == CfgNodeType.FunctionEntry)
            {
                AnalyseFunctionEntry((FunEntryBlock)node, inSet);
                OutSets[node] = inSet;
            }
            else if (node.Type == CfgNodeType.BasicBlock)
            {
                ScrClass? currentClass = NodeToClassMap.GetValueOrDefault(node);
                SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass);

                AnalyseBasicBlock((BasicBlock)node, symbolTable);
                OutSets[node] = symbolTable.VariableSymbols;
            }
            else if (node.Type == CfgNodeType.ClassMembersBlock)
            {
                ScrClass? currentClass = NodeToClassMap.GetValueOrDefault(node);
                SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass);

                AnalyseClassMembersBlock((ClassMembersBlock)node, symbolTable);
                OutSets[node] = symbolTable.VariableSymbols;
            }
            else if (node.Type == CfgNodeType.EnumerationNode)
            {
                ScrClass? currentClass = NodeToClassMap.GetValueOrDefault(node);
                SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass);

                AnalyseEnumeration((EnumerationNode)node, symbolTable);
                OutSets[node] = symbolTable.VariableSymbols;
            }
            else if (node.Type == CfgNodeType.IterationNode)
            {
                ScrClass? currentClass = NodeToClassMap.GetValueOrDefault(node);
                SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass);

                AnalyseIteration((IterationNode)node, symbolTable);
                OutSets[node] = symbolTable.VariableSymbols;
            }
            else if (node.Type == CfgNodeType.DecisionNode)
            {
                ScrClass? currentClass = NodeToClassMap.GetValueOrDefault(node);
                SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass);

                AnalyseDecision((DecisionNode)node, symbolTable);
                OutSets[node] = symbolTable.VariableSymbols;
            }
            else if (node.Type == CfgNodeType.SwitchNode)
            {
                ScrClass? currentClass = NodeToClassMap.GetValueOrDefault(node);
                SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass);

                AnalyseSwitch((SwitchNode)node, symbolTable);
                OutSets[node] = symbolTable.VariableSymbols;
            }
            else if (node.Type == CfgNodeType.SwitchCaseDecisionNode)
            {
                ScrClass? currentClass = NodeToClassMap.GetValueOrDefault(node);
                SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass);

                AnalyseSwitchCaseDecision((SwitchCaseDecisionNode)node, symbolTable);
                OutSets[node] = symbolTable.VariableSymbols;
            }
            else
            {
                // TODO: temp - just copy the in set to the out set.
                OutSets[node] = inSet;
            }

            // Check if the outset has changed before queueing successors.
            bool outSetChanged = previousOutSet == null || !previousOutSet.VariableTableEquals(OutSets[node]);

            // Only add successors to the worklist if the outset has changed
            if (!outSetChanged)
            {
                continue;
            }

            foreach (CfgNode successor in node.Outgoing)
            {
                worklist.Push(successor);
            }
        }

        // Check if we hit the iteration limit
        if (iterations >= maxIterations)
        {
            Log.Warning("Reaching definitions analysis hit iteration limit ({maxIterations}) for class {className}. This may indicate convergence issues.",
                maxIterations, scrClass.Name ?? "<anonymous>");
        }

        // Now that analysis is done, do one final pass to add diagnostics.
        Silent = false;

        foreach (CfgNode node in visited)
        {
            if (!InSets.TryGetValue(node, out Dictionary<string, ScrVariable>? inSet))
            {
                continue;
            }

            // Re-run analysis with Silent = false to generate diagnostics
            ScrClass? currentClass = NodeToClassMap.GetValueOrDefault(node);
            switch (node.Type)
            {
                case CfgNodeType.BasicBlock:
                    AnalyseBasicBlock((BasicBlock)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass));
                    break;
                case CfgNodeType.ClassMembersBlock:
                    AnalyseClassMembersBlock((ClassMembersBlock)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass));
                    break;
                case CfgNodeType.EnumerationNode:
                    AnalyseEnumeration((EnumerationNode)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass));
                    break;
                case CfgNodeType.IterationNode:
                    AnalyseIteration((IterationNode)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass));
                    break;
                case CfgNodeType.SwitchNode:
                    AnalyseSwitch((SwitchNode)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass));
                    break;
                case CfgNodeType.SwitchCaseDecisionNode:
                    AnalyseSwitchCaseDecision((SwitchCaseDecisionNode)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass));
                    break;
                case CfgNodeType.DecisionNode:
                    AnalyseDecision((DecisionNode)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass));
                    break;
            }
        }
    }

    private void AddFunctionReferenceToken(Token token, ScrFunction function, SymbolTable symbolTable)
    {
        bool isClassMethod = symbolTable.CurrentClass is not null &&
            symbolTable.CurrentClass.Methods.Any(m => m == function);

        if (isClassMethod)
            Sense.AddSenseToken(token, new ScrMethodReferenceSymbol(token, function, symbolTable.CurrentClass!));
        else
            Sense.AddSenseToken(token, new ScrFunctionReferenceSymbol(token, function));
    }

    private void ValidateArgumentCount(ScrFunction? function, int argCount, Range callRange, string functionName)
    {
        // Check if we have function information
        if (function is null)
        {
            return; // No function info, can't validate
        }

        // Check if we have any overload information
        if (function.Overloads is null || function.Overloads.Count == 0)
        {
            return; // No signature info, can't validate
        }

        // For now, just validate against the first overload
        // TODO: Check all overloads and find best match
        ScrFunctionOverload overload = function.Overloads[0];

        // Parameters can be null in some cases (e.g., API functions)
        if (overload?.Parameters is null)
        {
            return;
        }

        int minArgs = overload.Parameters.Count(p => p.Mandatory == true);
        int maxArgs = overload.Parameters.Count;
        bool hasVararg = overload.Vararg;

        // TODO: Re-enable argument count diagnostics when ready
        // If function has vararg, it accepts minArgs or more
        // if (hasVararg)
        // {
        //     if (argCount < minArgs)
        //     {
        //         AddDiagnostic(callRange, GSCErrorCodes.TooFewArguments, functionName, argCount, minArgs);
        //     }
        //     // No upper limit with vararg
        //     return;
        // }

        // Without vararg, check bounds
        // if (argCount < minArgs)
        // {
        //     AddDiagnostic(callRange, GSCErrorCodes.TooFewArguments, functionName, argCount, minArgs);
        // }
        // else if (argCount > maxArgs)
        // {
        //     AddDiagnostic(callRange, GSCErrorCodes.TooManyArguments, functionName, argCount, maxArgs);
        // }
    }

    private void ValidateExpressionHasSideEffects(ExprNode expr)
    {
        // Check if this expression has side effects (similar to expression statement validation)
        bool hasSideEffects = expr.OperatorType switch
        {
            ExprOperatorType.Binary when expr is BinaryExprNode binaryExpr =>
                binaryExpr.Operation == TokenType.Assign ||
                binaryExpr.Operation == TokenType.PlusAssign ||
                binaryExpr.Operation == TokenType.MinusAssign ||
                binaryExpr.Operation == TokenType.MultiplyAssign ||
                binaryExpr.Operation == TokenType.DivideAssign ||
                binaryExpr.Operation == TokenType.ModuloAssign ||
                binaryExpr.Operation == TokenType.BitAndAssign ||
                binaryExpr.Operation == TokenType.BitOrAssign ||
                binaryExpr.Operation == TokenType.BitXorAssign ||
                binaryExpr.Operation == TokenType.BitLeftShiftAssign ||
                binaryExpr.Operation == TokenType.BitRightShiftAssign,

            ExprOperatorType.Postfix when expr is PostfixExprNode postfixExpr =>
                postfixExpr.Operator.Type == TokenType.Increment ||
                postfixExpr.Operator.Type == TokenType.Decrement,

            ExprOperatorType.FunctionCall => true,
            ExprOperatorType.MethodCall => true,
            ExprOperatorType.CallOn => true,

            _ => false
        };

        if (!hasSideEffects)
        {
            AddDiagnostic(expr.Range, GSCErrorCodes.InvalidExpressionStatement);
        }
    }

    public void AnalyseFunctionEntry(FunEntryBlock entry, Dictionary<string, ScrVariable> inSet)
    {
        FunDefnNode? node = entry.Source;

        // Handle constructor/destructor entries (which have null source)
        if (node is null)
        {
            return;
        }

        // Add the function's parameters to the in set.
        foreach (ParamNode param in node.Parameters.Parameters)
        {
            if (param.Name is null)
            {
                continue;
            }

            inSet[param.Name.Lexeme] = new(param.Name.Lexeme, ScrData.Default, 0, false);
        }

        // Note: Built-in globals (self, level, game, anim) are no longer added to the symbol table.
        // They are implicitly available but should be handled separately if needed.

        if (node.Parameters.Vararg)
        {
            inSet["vararg"] = new("vararg", new ScrData(ScrDataTypes.Array), 0, true);
        }
    }

    public void AnalyseEnumeration(EnumerationNode node, SymbolTable symbolTable)
    {
        ForeachStmtNode foreachStmt = node.Source;

        // Nothing to work with, errored earlier on.
        if (foreachStmt.Collection is null)
        {
            return;
        }

        // Analyse the collection.
        ScrData collection = AnalyseExpr(foreachStmt.Collection, symbolTable, Sense);

        if (!collection.TypeUnknown() && collection.Type != ScrDataTypes.Array)
        {
            AddDiagnostic(foreachStmt.Collection.Range, GSCErrorCodes.CannotEnumerateType, collection.TypeToString());
        }

        if (foreachStmt.KeyIdentifier is not null)
        {
            Token keyIdentifier = foreachStmt.KeyIdentifier.Token;
            AssignmentResult keyAssignmentResult = symbolTable.AddOrSetVariableSymbol(keyIdentifier.Lexeme, ScrData.Default);

            if (keyAssignmentResult == AssignmentResult.SuccessNew)
            {
                Sense.AddSenseToken(keyIdentifier, ScrVariableSymbol.Declaration(foreachStmt.KeyIdentifier, ScrData.Default));
            }
        }

        Token valueIdentifier = foreachStmt.ValueIdentifier.Token;
        AssignmentResult valueAssignmentResult = symbolTable.AddOrSetVariableSymbol(valueIdentifier.Lexeme, ScrData.Default);

        // if (!assignmentResult)
        // {
        //     // TODO: how does GSC handle this?
        // }
        if (valueAssignmentResult == AssignmentResult.SuccessNew)
        {
            Sense.AddSenseToken(valueIdentifier, ScrVariableSymbol.Declaration(foreachStmt.ValueIdentifier, ScrData.Default));
        }
        else if (valueAssignmentResult == AssignmentResult.SuccessMutated)
        {
            Sense.AddSenseToken(valueIdentifier, ScrVariableSymbol.Usage(foreachStmt.ValueIdentifier, ScrData.Default));
        }
    }

    public void AnalyseIteration(IterationNode node, SymbolTable symbolTable)
    {
        // Analyse the initialisation.
        if (node.Initialisation is not null)
        {
            ScrData initialisation = AnalyseExpr(node.Initialisation, symbolTable, Sense);

            // For loop initialization should follow the same rules as expression statements
            // Only assignments, calls, increments, decrements should be allowed
            ValidateExpressionHasSideEffects(node.Initialisation);
        }

        // Analyse the condition if present
        if (node.Condition is not null)
        {
            ScrData condition = AnalyseExpr(node.Condition, symbolTable, Sense);

            if (!condition.TypeUnknown() && !condition.CanEvaluateToBoolean())
            {
                AddDiagnostic(node.Condition.Range, GSCErrorCodes.NoImplicitConversionExists, condition.TypeToString(), ScrDataTypeNames.Bool);
            }
        }

        // Analyse the increment if present
        if (node.Increment is not null)
        {
            ScrData increment = AnalyseExpr(node.Increment, symbolTable, Sense);

            // For loop increment should also follow expression statement rules
            ValidateExpressionHasSideEffects(node.Increment);
        }
    }

    public void AnalyseDecision(DecisionNode node, SymbolTable symbolTable)
    {
        DecisionAstNode decision = node.Source;

        // It either errored or it's an else, nothing to do.
        if (decision.Condition is null)
        {
            return;
        }

        // Analyse the condition.
        ScrData condition = AnalyseExpr(decision.Condition, symbolTable, Sense);

        if (!condition.TypeUnknown() && !condition.CanEvaluateToBoolean())
        {
            AddDiagnostic(decision.Condition.Range, GSCErrorCodes.NoImplicitConversionExists, condition.TypeToString(), ScrDataTypeNames.Bool);
        }

        // TODO: if the condition evaluates to false, then mark the block that follows as unreachable.
        // TODO: if the condition evaluates to true, then any else blocks are unreachable.
    }

    public void AnalyseSwitch(SwitchNode node, SymbolTable symbolTable)
    {
        // Create context for this switch (only once, even if revisited)
        if (!SwitchContexts.ContainsKey(node))
        {
            var context = new SwitchAnalysisContext();

            // Analyze expression ONCE and cache it
            if (node.Source.Expression is not null)
            {
                context.SwitchExpressionType = AnalyseExpr(node.Source.Expression, symbolTable, Sense);
            }

            SwitchContexts[node] = context;
        }
    }

    public void AnalyseSwitchCaseDecision(SwitchCaseDecisionNode node, SymbolTable symbolTable)
    {
        // Look up the switch context (guaranteed to exist because worklist processes SwitchNode first)
        if (!SwitchContexts.TryGetValue(node.ParentSwitch, out var context))
        {
            return; // Defensive: shouldn't happen
        }

        // Check if we've already analyzed this specific node's labels
        bool isFirstTimeAnalyzingThisNode = context.AnalyzedNodes.Add(node);

        ScrData switchType = context.SwitchExpressionType;

        foreach (CaseLabelNode label in node.Labels)
        {
            if (label.NodeType == AstNodeType.DefaultLabel)
            {
                // Only update state on first analysis of this node
                if (isFirstTimeAnalyzingThisNode)
                {
                    if (context.HasDefault)
                    {
                        // Duplicate default (already caught in CFG construction, but could warn again if needed)
                    }
                    context.HasDefault = true;
                }
                continue;
            }

            if (label.Value is null) continue;

            // Analyze the label value
            ScrData labelType = AnalyseExpr(label.Value, symbolTable, Sense);

            // Type compatibility check - always check since types can change during analysis
            if (!AreTypesCompatibleForSwitch(switchType, labelType))
            {
                if (!Silent) // Only emit in diagnostic pass
                {
                    AddDiagnostic(label.Value.Range, GSCErrorCodes.UnreachableCase);
                }
            }

            // TODO: this isn't working at the moment.
            // Duplicate label check - only on first analysis of this node
            if (isFirstTimeAnalyzingThisNode && TryGetCaseLabelValueKey(label.Value, out string key))
            {
                if (!context.SeenLabelValues.Add(key))
                {
                    if (!Silent) // Only emit in diagnostic pass
                    {
                        AddDiagnostic(label.Value.Range, GSCErrorCodes.DuplicateCaseLabel);
                    }
                }
            }
        }
    }

    private bool AreTypesCompatibleForSwitch(ScrData switchType, ScrData labelType)
    {
        // If either type is unknown, assume compatible
        if (switchType.TypeUnknown() || labelType.TypeUnknown())
        {
            return true;
        }

        // TODO: Implement proper type compatibility rules for switch statements
        // For now, allow any comparison (GSC is weakly typed)
        return true;
    }

    private bool TryGetCaseLabelValueKey(ExprNode expr, out string key)
    {
        key = string.Empty;

        if (expr is DataExprNode dataExpr)
        {
            // Encode type in key to avoid collisions between e.g., string "1" and int 1
            key = dataExpr.Type switch
            {
                ScrDataTypes.Int => $"int:{dataExpr.Value}",
                ScrDataTypes.Float => $"float:{dataExpr.Value}",
                ScrDataTypes.String => $"str:{dataExpr.Value}",
                ScrDataTypes.IString => $"istr:{dataExpr.Value}",
                ScrDataTypes.Hash => $"hash:{dataExpr.Value}",
                ScrDataTypes.Bool => $"bool:{dataExpr.Value}",
                _ => string.Empty
            };
            return key.Length > 0;
        }

        return false;
    }

    public void AnalyseBasicBlock(BasicBlock block, SymbolTable symbolTable)
    {
        LinkedList<AstNode> logic = block.Statements;

        if (logic.Count == 0)
        {
            return;
        }

        for (LinkedListNode<AstNode>? node = logic.First; node != null; node = node.Next)
        {
            AstNode child = node.Value;

            AstNode? last = node.Previous?.Value;
            AstNode? next = node.Next?.Value;

            AnalyseStatement(child, last, next, symbolTable);
        }
    }

    public void AnalyseClassMembersBlock(ClassMembersBlock block, SymbolTable symbolTable)
    {
        LinkedList<AstNode> members = block.Statements;

        if (members.Count == 0)
        {
            return;
        }

        // Iterate through member declarations and add them to the symbol table
        for (LinkedListNode<AstNode>? node = members.First; node != null; node = node.Next)
        {
            AstNode child = node.Value;

            // Should only be member declarations in a ClassMembersBlock
            if (child is MemberDeclNode memberDecl)
            {
                AnalyseMemberDecl(memberDecl, symbolTable);
            }
        }
    }

    public void AnalyseMemberDecl(MemberDeclNode memberDecl, SymbolTable symbolTable)
    {
        if (memberDecl.NameToken is null)
        {
            return;
        }

        string memberName = memberDecl.NameToken.Lexeme;

        // Add the field to the symbol table with default type (fields can be any type in GSC)
        // Fields are like variables but at class scope (scope 0)
        AssignmentResult assignmentResult = symbolTable.TryAddVariableSymbol(memberName, ScrData.Default);

        if (assignmentResult == AssignmentResult.SuccessNew)
        {
            // Add a semantic token for the field declaration
            // Using a custom identifier expression node for the sense token
            IdentifierExprNode fieldIdentifier = new(memberDecl.NameToken);
            Sense.AddSenseToken(memberDecl.NameToken, ScrVariableSymbol.Declaration(fieldIdentifier, ScrData.Default));
            return;
        }

        if (assignmentResult == AssignmentResult.FailedReserved)
        {
            AddDiagnostic(memberDecl.NameToken.Range, GSCErrorCodes.ReservedSymbol, memberName);
            return;
        }

        // If not SuccessNew and not FailedReserved, it's a redefinition (FailedConstant or other)
        AddDiagnostic(memberDecl.NameToken.Range, GSCErrorCodes.RedefinitionOfSymbol, memberName);
    }

    public void AnalyseStatement(AstNode statement, AstNode? last, AstNode? next, SymbolTable symbolTable)
    {
        switch (statement.NodeType)
        {
            case AstNodeType.ExprStmt:
                AnalyseExprStmt((ExprStmtNode)statement, last, next, symbolTable);
                break;
            case AstNodeType.ConstStmt:
                AnalyseConstStmt((ConstStmtNode)statement, last, next, symbolTable);
                break;
            case AstNodeType.ReturnStmt:
                AnalyseReturnStmt((ReturnStmtNode)statement, symbolTable);
                break;
            case AstNodeType.WaitStmt:
                AnalyseWaitStmt((ReservedFuncStmtNode)statement, symbolTable);
                break;
            case AstNodeType.WaitRealTimeStmt:
                AnalyseWaitStmt((ReservedFuncStmtNode)statement, symbolTable);
                break;
            default:
                break;
        }
    }

    public void AnalyseExprStmt(ExprStmtNode statement, AstNode? last, AstNode? next, SymbolTable symbolTable)
    {
        if (statement.Expr is null)
        {
            return;
        }

        ScrData result = AnalyseExpr(statement.Expr, symbolTable, Sense);
    }

    public void AnalyseConstStmt(ConstStmtNode statement, AstNode? last, AstNode? next, SymbolTable symbolTable)
    {
        if (statement.Value is null)
        {
            return;
        }

        ScrData result = AnalyseExpr(statement.Value, symbolTable, Sense);

        // Assign the result to the symbol table.
        AssignmentResult assignmentResult = symbolTable.TryAddVariableSymbol(statement.Identifier, result with { ReadOnly = true });

        if (assignmentResult == AssignmentResult.SuccessNew)
        {
            // Add a semantic token for the constant.
            Sense.AddSenseToken(statement.IdentifierToken, ScrVariableSymbol.ConstantDeclaration(statement.IdentifierToken, result));
            return;
        }
        else if (assignmentResult == AssignmentResult.FailedReserved)
        {
            AddDiagnostic(statement.Range, GSCErrorCodes.ReservedSymbol, statement.Identifier);
            return;
        }

        AddDiagnostic(statement.Range, GSCErrorCodes.RedefinitionOfSymbol, statement.Identifier);
    }

    public void AnalyseReturnStmt(ReturnStmtNode statement, SymbolTable symbolTable)
    {
        // If there's a return value, analyze it
        if (statement.Value is not null)
        {
            ScrData result = AnalyseExpr(statement.Value, symbolTable, Sense);
            // TODO: Could validate return type matches function signature if available
        }
    }

    public void AnalyseWaitStmt(ReservedFuncStmtNode statement, SymbolTable symbolTable)
    {
        // Wait/WaitRealTime statements must have a duration expression
        if (statement.Expr is null)
        {
            return;
        }

        ScrData duration = AnalyseExpr(statement.Expr, symbolTable, Sense);

        // Duration must be numeric (int or float) or any
        if (!duration.TypeUnknown() && !duration.IsNumeric())
        {
            AddDiagnostic(statement.Expr.Range, GSCErrorCodes.NoImplicitConversionExists,
                duration.TypeToString(), ScrDataTypeNames.Int, ScrDataTypeNames.Float);
        }
    }

    private ScrData AnalyseExpr(ExprNode expr, SymbolTable symbolTable, ParserIntelliSense sense, bool createSenseTokenForRhs = true)
    {
        return expr.OperatorType switch
        {
            ExprOperatorType.Binary when expr is NamespacedMemberNode namespaceMember => AnalyseScopeResolution(namespaceMember, symbolTable, sense),
            ExprOperatorType.Binary => AnalyseBinaryExpr((BinaryExprNode)expr, symbolTable, createSenseTokenForRhs),
            ExprOperatorType.Prefix => AnalysePrefixExpr((PrefixExprNode)expr, symbolTable, sense),
            ExprOperatorType.Postfix => AnalysePostfixExpr((PostfixExprNode)expr, symbolTable, sense),
            ExprOperatorType.DataOperand => AnalyseDataExpr((DataExprNode)expr),
            ExprOperatorType.IdentifierOperand => AnalyseIdentifierExpr((IdentifierExprNode)expr, symbolTable, createSenseTokenForRhs),
            ExprOperatorType.Vector => AnalyseVectorExpr((VectorExprNode)expr, symbolTable),
            ExprOperatorType.Indexer => AnalyseIndexerExpr((ArrayIndexNode)expr, symbolTable),
            ExprOperatorType.CallOn => AnalyseCallOnExpr((CalledOnNode)expr, symbolTable),
            ExprOperatorType.FunctionCall => AnalyseFunctionCall((FunCallNode)expr, symbolTable, sense),
            ExprOperatorType.Constructor => AnalyseConstructorExpr((ConstructorExprNode)expr, symbolTable),
            ExprOperatorType.Waittill => AnalyseWaittillExpr((WaittillNode)expr, symbolTable, sense),
            ExprOperatorType.Ternary => AnalyseTernaryExpr((TernaryExprNode)expr, symbolTable, sense),
            ExprOperatorType.MethodCall => AnalyseMethodCall((MethodCallNode)expr, symbolTable, sense),
            _ => ScrData.Default,
        };
    }

    private ScrData AnalyseWaittillExpr(WaittillNode expr, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        ScrData notifyCondition = AnalyseExpr(expr.NotifyCondition, symbolTable, sense);
        ScrData entity = AnalyseExpr(expr.Entity, symbolTable, sense);

        // The called-on must be an entity.
        if (entity.Type != ScrDataTypes.Entity && !entity.IsAny())
        {
            AddDiagnostic(expr.Entity.Range, GSCErrorCodes.NoImplicitConversionExists, entity.TypeToString(), ScrDataTypeNames.Entity);
            return ScrData.Default;
        }

        // The notify condition must be a string or hash.
        if (notifyCondition.Type != ScrDataTypes.String && notifyCondition.Type != ScrDataTypes.Hash && !notifyCondition.IsAny())
        {
            AddDiagnostic(expr.NotifyCondition.Range, GSCErrorCodes.NoImplicitConversionExists, notifyCondition.TypeToString(), ScrDataTypeNames.String, ScrDataTypeNames.Hash);
            return ScrData.Default;
        }

        // Now emit the variables, all as type any.
        foreach (IdentifierExprNode variable in expr.Variables.Variables)
        {
            symbolTable.AddOrSetVariableSymbol(variable.Identifier, ScrData.Default);
            Sense.AddSenseToken(variable.Token, ScrVariableSymbol.Declaration(variable, ScrData.Default));
        }

        // Waittill doesn't return.
        return ScrData.Void;
    }

    private ScrData AnalyseConstructorExpr(ConstructorExprNode constructor, SymbolTable symbolTable)
    {
        Token classIdentifier = constructor.Identifier;
        string className = classIdentifier.Lexeme;

        if (symbolTable.GlobalSymbolTable.TryGetValue(className, out IExportedSymbol? exportedSymbol) &&
            exportedSymbol is ScrClass scrClass)
        {
            Sense.AddSenseToken(classIdentifier, new ScrClassSymbol(classIdentifier, scrClass));
            return ScrData.Default;
        }

        AddDiagnostic(classIdentifier.Range, GSCErrorCodes.NotDefined, className);
        return ScrData.Default;
    }

    private ScrData AnalyseTernaryExpr(TernaryExprNode ternary, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        // Analyze the condition
        ScrData condition = AnalyseExpr(ternary.Condition, symbolTable, sense);

        // Validate that the condition can be evaluated to a boolean
        if (!condition.TypeUnknown() && !condition.CanEvaluateToBoolean())
        {
            AddDiagnostic(ternary.Condition.Range, GSCErrorCodes.NoImplicitConversionExists, condition.TypeToString(), ScrDataTypeNames.Bool);
        }

        // Check if we can statically determine the result of the condition
        bool? truthy = condition.IsTruthy();

        ScrData trueResult = ScrData.Default;
        if (ternary.Then is not null)
        {
            // If we know the condition is false, we technically don't need to analyze the true branch for values,
            // but we might still want to analyze it for side effects or diagnostics?
            // In DFA, we usually skip unreachable code's effect on flow, but here we are in an expression analyzer.
            // For now, let's analyze both to ensure we catch errors in both branches, but we optimize the return value.
            trueResult = AnalyseExpr(ternary.Then, symbolTable, sense);
        }

        ScrData falseResult = ScrData.Default;
        if (ternary.Else is not null)
        {
            falseResult = AnalyseExpr(ternary.Else, symbolTable, sense);
        }

        // If the condition is known at compile time, return only the taken branch's data
        if (truthy.HasValue)
        {
            return truthy.Value ? trueResult : falseResult;
        }

        // Otherwise, return the merge of both branches
        return ScrData.Merge(trueResult, falseResult);
    }

    private ScrData AnalyseMethodCall(MethodCallNode methodCall, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        // 1. Analyze the target (LHS of ->)
        // If target is null (implicit 'this'), we treat it as if 'self' was the target
        ScrData target = methodCall.Target is not null
            ? AnalyseExpr(methodCall.Target, symbolTable, sense)
            : new ScrData(ScrDataTypes.Entity); // Assuming 'self' is an entity/object

        // 2. Validate target type
        // The target must be an Object or Any.
        if (!target.TypeUnknown() &&
            target.Type != ScrDataTypes.Object)
        {
            // If the target isn't a valid object type, we can't call methods on it.
            // We only warn if we're sure it's the wrong type (not Any).
            AddDiagnostic(methodCall.Target?.Range ?? methodCall.Range,
                GSCErrorCodes.NoImplicitConversionExists,
                target.TypeToString(),
                ScrDataTypeNames.Object);
        }

        // 3. Analyze arguments
        // We do this even if the target is invalid, to ensure side effects in arguments are processed
        foreach (ExprNode? argument in methodCall.Arguments.Arguments)
        {
            if (argument is null) continue;
            AnalyseExpr(argument, symbolTable, sense);
        }

        // 4. Resolve the method symbol (if possible)
        // For now, we assume the method exists on the target.
        // We're just "superficially" analyzing, so we don't look up the specific method definition yet
        // to validate signature or return type.

        return ScrData.Default;
    }

    private ScrData AnalyseBinaryExpr(BinaryExprNode binary, SymbolTable symbolTable, bool createSenseTokenForRhs = true)
    {
        if (binary.Operation == TokenType.Dot)
        {
            return AnalyseDotOp(binary, symbolTable, createSenseTokenForRhs);
        }
        if (binary.Operation == TokenType.Assign)
        {
            return AnalyseAssignOp(binary, symbolTable);
        }
        if (binary.Operation == TokenType.PlusAssign)
        {
            return AnalysePlusAssignOp(binary, symbolTable);
        }
        if (binary.Operation == TokenType.MinusAssign)
        {
            return AnalyseCompoundAssignOp(binary, symbolTable, TokenType.MinusAssign);
        }
        if (binary.Operation == TokenType.MultiplyAssign)
        {
            return AnalyseCompoundAssignOp(binary, symbolTable, TokenType.MultiplyAssign);
        }
        if (binary.Operation == TokenType.DivideAssign)
        {
            return AnalyseCompoundAssignOp(binary, symbolTable, TokenType.DivideAssign);
        }
        if (binary.Operation == TokenType.ModuloAssign)
        {
            return AnalyseCompoundAssignOp(binary, symbolTable, TokenType.ModuloAssign);
        }
        if (binary.Operation == TokenType.BitAndAssign)
        {
            return AnalyseCompoundAssignOp(binary, symbolTable, TokenType.BitAndAssign);
        }
        if (binary.Operation == TokenType.BitOrAssign)
        {
            return AnalyseCompoundAssignOp(binary, symbolTable, TokenType.BitOrAssign);
        }
        if (binary.Operation == TokenType.BitXorAssign)
        {
            return AnalyseCompoundAssignOp(binary, symbolTable, TokenType.BitXorAssign);
        }
        if (binary.Operation == TokenType.BitLeftShiftAssign)
        {
            return AnalyseCompoundAssignOp(binary, symbolTable, TokenType.BitLeftShiftAssign);
        }
        if (binary.Operation == TokenType.BitRightShiftAssign)
        {
            return AnalyseCompoundAssignOp(binary, symbolTable, TokenType.BitRightShiftAssign);
        }

        ScrData left = AnalyseExpr(binary.Left!, symbolTable, Sense);
        ScrData right = AnalyseExpr(binary.Right!, symbolTable, Sense);

        return binary.Operation switch
        {
            TokenType.Plus => AnalyseAddOp(binary, left, right),
            TokenType.Minus => AnalyseMinusOp(binary, left, right),
            TokenType.Multiply => AnalyseMultiplyOp(binary, left, right),
            TokenType.Divide => AnalyseDivideOp(binary, left, right),
            TokenType.Modulo => AnalyseModuloOp(binary, left, right),
            TokenType.BitLeftShift => AnalyseBitLeftShiftOp(binary, left, right),
            TokenType.BitRightShift => AnalyseBitRightShiftOp(binary, left, right),
            TokenType.GreaterThan => AnalyseGreaterThanOp(binary, left, right),
            TokenType.LessThan => AnalyseLessThanOp(binary, left, right),
            TokenType.GreaterThanEquals => AnalyseGreaterThanEqualsOp(binary, left, right),
            TokenType.LessThanEquals => AnalyseLessThanEqualsOp(binary, left, right),
            TokenType.BitAnd => AnalyseBitAndOp(binary, left, right),
            TokenType.BitOr => AnalyseBitOrOp(binary, left, right),
            TokenType.BitXor => AnalyseBitXorOp(binary, left, right),
            TokenType.Equals => AnalyseEqualsOp(binary, left, right),
            TokenType.NotEquals => AnalyseNotEqualsOp(binary, left, right),
            TokenType.IdentityEquals => AnalyseIdentityEqualsOp(binary, left, right),
            TokenType.IdentityNotEquals => AnalyseIdentityNotEqualsOp(binary, left, right),
            TokenType.And => AnalyseAndOp(binary, left, right),
            TokenType.Or => AnalyseOrOp(binary, left, right),
            _ => ScrData.Default,
        };

        // TODO: Binary operators not yet mapped:
        // - Arrow (->)
        // Assignment operators:
        // - PlusAssign (+=)
        // - MinusAssign (-=)
        // - MultiplyAssign (*=)
        // - DivideAssign (/=)
        // - ModuloAssign (%=)
        // - BitAndAssign (&=)
        // - BitOrAssign (|=)
        // - BitXorAssign (^=)
        // - BitLeftShiftAssign (<<=)
        // - BitRightShiftAssign (>>=)
    }

    private ScrData AnalysePrefixExpr(PrefixExprNode prefix, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        return prefix.Operation switch
        {
            TokenType.Thread => AnalyseThreadedFunctionCall(prefix, symbolTable, sense),
            TokenType.BitAnd => AnalyseFunctionPointer(prefix, symbolTable, sense),
            TokenType.Not => AnalyseNotOp(prefix, symbolTable, sense),
            TokenType.Minus => AnalyseNegationOp(prefix, symbolTable, sense),
            _ => ScrData.Default,
        };
    }

    /// <summary>
    /// Analyzes a function pointer expression (e.g., &functionName).
    /// This looks up the function in the global function table and returns a FunctionPointer type.
    /// </summary>
    private ScrData AnalyseFunctionPointer(PrefixExprNode prefix, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        // The operand should be an identifier
        if (prefix.Operand is not IdentifierExprNode identifier)
        {
            // For now, just analyze the operand normally
            return AnalyseExpr(prefix.Operand!, symbolTable, sense);
        }

        // Look up the function in the global function table
        ScrData functionData = symbolTable.TryGetFunction(identifier.Identifier, out SymbolFlags flags);

        if (functionData.Type == ScrDataTypes.Undefined)
        {
            AddDiagnostic(identifier.Range, GSCErrorCodes.FunctionDoesNotExist, identifier.Identifier);
            return ScrData.Undefined();
        }

        if (functionData.Type != ScrDataTypes.Function)
        {
            AddDiagnostic(identifier.Range, GSCErrorCodes.ExpectedFunction, functionData.TypeToString());
            return ScrData.Undefined();
        }

        // Add sense token for the function reference
        if (!flags.HasFlag(SymbolFlags.Reserved) && !functionData.ValueUnknown())
        {
            AddFunctionReferenceToken(identifier.Token, functionData.Get<ScrFunction>(), symbolTable);
        }

        // Return as a FunctionPointer type (a pointer to the function, not the function itself)
        return new ScrData(ScrDataTypes.FunctionPointer, functionData.Value);
    }

    private ScrData AnalyseNotOp(PrefixExprNode prefix, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        ScrData operand = AnalyseExpr(prefix.Operand!, symbolTable, sense);

        // Needs to be a boolean, or at least can be coerced to one.
        if (!operand.CanEvaluateToBoolean() && !operand.IsAny())
        {
            AddDiagnostic(prefix.Operand!.Range, GSCErrorCodes.NoImplicitConversionExists, operand.TypeToString(), ScrDataTypeNames.Bool);
            return ScrData.Default;
        }

        bool? truthy = operand.IsTruthy();

        // Value not known, so just return bool.
        if (truthy is null)
        {
            return new ScrData(ScrDataTypes.Bool);
        }

        return new ScrData(ScrDataTypes.Bool, !truthy.Value);
    }

    private ScrData AnalyseNegationOp(PrefixExprNode prefix, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        ScrData operand = AnalyseExpr(prefix.Operand!, symbolTable, sense);

        if (operand.TypeUnknown())
        {
            return ScrData.Default;
        }

        if (operand.IsAny())
        {
            return ScrData.Default;
        }

        if (!operand.IsNumeric())
        {
            AddDiagnostic(prefix.Range, GSCErrorCodes.NoImplicitConversionExists,
                operand.TypeToString(), ScrDataTypeNames.Int, ScrDataTypeNames.Float);
            return ScrData.Default;
        }

        if (operand.Type == ScrDataTypes.Int)
        {
            if (operand.ValueUnknown())
            {
                return new ScrData(ScrDataTypes.Int);
            }

            int? value = operand.GetIntegerValue();
            return value.HasValue ? new ScrData(ScrDataTypes.Int, -value.Value) : new ScrData(ScrDataTypes.Int);
        }

        // Must be float if numeric and not int
        if (operand.ValueUnknown())
        {
            return new ScrData(ScrDataTypes.Float);
        }

        float? numericValue = operand.GetNumericValue();
        return numericValue.HasValue ? new ScrData(ScrDataTypes.Float, -numericValue.Value) : new ScrData(ScrDataTypes.Float);
    }

    private ScrData AnalysePostfixExpr(PostfixExprNode postfix, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        return postfix.Operation switch
        {
            TokenType.Increment => AnalysePostIncrementOp(postfix, symbolTable, sense),
            TokenType.Decrement => AnalysePostDecrementOp(postfix, symbolTable, sense),
            _ => ScrData.Default,
        };
    }

    /// <summary>
    /// Helper method to perform assignment to either a local variable or a struct property.
    /// Handles read-only validation, symbol table updates, and sense token generation.
    /// </summary>
    /// <param name="operand">The expression node representing the assignment target (identifier or dot expression)</param>
    /// <param name="target">The analyzed ScrData of the target before assignment</param>
    /// <param name="newValue">The new value to assign</param>
    /// <param name="symbolTable">The symbol table to update</param>
    /// <returns>True if assignment was successful, false if it failed (e.g., read-only)</returns>
    private bool TryAssignToTarget(ExprNode operand, ScrData target, ScrData newValue, SymbolTable symbolTable)
    {
        // Assigning to a local variable
        if (operand is IdentifierExprNode identifier)
        {
            string symbolName = identifier.Identifier;

            if (target.ReadOnly)
            {
                AddDiagnostic(identifier.Range, GSCErrorCodes.CannotAssignToConstant, symbolName);
                return false;
            }

            symbolTable.SetSymbol(symbolName, newValue);
            Sense.AddSenseToken(identifier.Token, ScrVariableSymbol.Usage(identifier, newValue));
            return true;
        }

        // Assigning to a property on a struct
        if (operand is BinaryExprNode binaryExprNode && binaryExprNode.Operation == TokenType.Dot && target.Owner is ScrStruct destination)
        {
            string fieldName = target.FieldName ?? throw new NullReferenceException("Sanity check failed: Target data has no field name.");

            if (target.ReadOnly)
            {
                AddDiagnostic(binaryExprNode.Right!.Range, GSCErrorCodes.CannotAssignToReadOnlyProperty, fieldName);
                return false;
            }

            destination.Set(fieldName, newValue);

            if (binaryExprNode.Right is IdentifierExprNode identifierNode)
            {
                bool isClassMember = symbolTable.CurrentClass is not null &&
                    symbolTable.CurrentClass.Members.Any(m => m.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));

                if (isClassMember)
                    Sense.AddSenseToken(identifierNode.Token, new ScrClassPropertySymbol(identifierNode, newValue, symbolTable.CurrentClass!));
                else
                    Sense.AddSenseToken(identifierNode.Token, new ScrFieldSymbol(identifierNode, newValue));
            }

            return true;
        }

        // Unsupported assignment target
        return false;
    }

    private ScrData AnalysePostIncrementOp(PostfixExprNode postfix, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        ScrData target = AnalyseExpr(postfix.Operand!, symbolTable, sense, false);

        // TODO: Does it have to be int, or can it also be float?
        if (target.Type != ScrDataTypes.Int && !target.IsAny())
        {
            AddDiagnostic(postfix.Operand!.Range, GSCErrorCodes.NoImplicitConversionExists, target.TypeToString(), ScrDataTypeNames.Int);
            return ScrData.Default;
        }

        // Calculate the incremented value
        ScrData incrementedValue = target.ValueUnknown()
            ? new ScrData(target.Type)
            : new ScrData(target.Type, target.Get<int>() + 1);

        // Perform the assignment using the shared helper
        TryAssignToTarget(postfix.Operand!, target, incrementedValue, symbolTable);

        // Return its old value (post-increment returns the value before incrementing)
        return target;
    }

    private ScrData AnalysePostDecrementOp(PostfixExprNode postfix, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        ScrData target = AnalyseExpr(postfix.Operand!, symbolTable, sense, false);

        // TODO: Does it have to be int, or can it also be float?
        if (target.Type != ScrDataTypes.Int && !target.IsAny())
        {
            AddDiagnostic(postfix.Operand!.Range, GSCErrorCodes.NoImplicitConversionExists, target.TypeToString(), ScrDataTypeNames.Int);
            return ScrData.Default;
        }

        // Calculate the decremented value
        ScrData decrementedValue = target.ValueUnknown()
            ? new ScrData(target.Type)
            : new ScrData(target.Type, target.Get<int>() - 1);

        // Perform the assignment using the shared helper
        TryAssignToTarget(postfix.Operand!, target, decrementedValue, symbolTable);

        // Return its old value (post-decrement returns the value before decrementing)
        return target;
    }

    private ScrData AnalyseAssignOp(BinaryExprNode node, SymbolTable symbolTable)
    {
        // TODO: decouple this from the general binary handler, then when it's an identifier, manually resolve either it to be a declaration
        // or a mutation. Check for const too.
        // actually, might not be necessary. undefined doesn't highlight, so the isNew check should suffice.
        ScrData left = AnalyseExpr(node.Left!, symbolTable, Sense, false);
        ScrData right = AnalyseExpr(node.Right!, symbolTable, Sense);

        // Assigning to a local variable or class member
        if (node.Left is IdentifierExprNode identifier)
        {
            string symbolName = identifier.Identifier;

            // Check if this is a class member being assigned
            bool isClassMember = symbolTable.CurrentClass is not null &&
                symbolTable.CurrentClass.Members.Any(m => m.Name.Equals(symbolName, StringComparison.OrdinalIgnoreCase));

            if (isClassMember)
            {
                // Assigning to a class member (implicit this.member)
                Sense.AddSenseToken(identifier.Token, new ScrClassPropertySymbol(identifier, right, symbolTable.CurrentClass!));
                return right;
            }

            if (left.ReadOnly)
            {
                AddDiagnostic(identifier.Range, GSCErrorCodes.CannotAssignToConstant, symbolName);
                return ScrData.Default;
            }

            if (right.Type == ScrDataTypes.Function)
            {
                AddDiagnostic(node.Right!.Range, GSCErrorCodes.StoreFunctionAsPointer);
                return ScrData.Default;
            }

            AssignmentResult assignmentResult = symbolTable.AddOrSetVariableSymbol(symbolName, right);

            if (right.Type == ScrDataTypes.Undefined)
            {
                return right;
            }

            // Failed, because the symbol is reserved
            if (assignmentResult == AssignmentResult.FailedReserved)
            {
                AddDiagnostic(identifier.Range, GSCErrorCodes.ReservedSymbol, symbolName);
                return ScrData.Default;
            }

            if (assignmentResult == AssignmentResult.SuccessNew)
            {
                Sense.AddSenseToken(identifier.Token, ScrVariableSymbol.Declaration(identifier, right));
                return right;
            }

            Sense.AddSenseToken(identifier.Token, ScrVariableSymbol.Usage(identifier, right));
            return right;
        }

        // Assigning to a property on a struct
        if (node.Left is BinaryExprNode binaryExprNode && binaryExprNode.Operation == TokenType.Dot && left.Owner is ScrStruct destination)
        {
            string fieldName = left.FieldName ?? throw new NullReferenceException("Sanity check failed: Left data has no field name.");

            if (left.ReadOnly)
            {
                AddDiagnostic(binaryExprNode.Right!.Range, GSCErrorCodes.CannotAssignToReadOnlyProperty, fieldName);
                return ScrData.Default;
            }

            destination.Set(fieldName, right);

            if (binaryExprNode.Right is IdentifierExprNode identifierNode)
            {
                bool isClassMember = symbolTable.CurrentClass is not null &&
                    symbolTable.CurrentClass.Members.Any(m => m.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));

                if (isClassMember)
                    Sense.AddSenseToken(identifierNode.Token, new ScrClassPropertySymbol(identifierNode, right, symbolTable.CurrentClass!));
                else
                    Sense.AddSenseToken(identifierNode.Token, new ScrFieldSymbol(identifierNode, right));
            }

            return right;
        }

        // TODO: once all cases are covered, we should enable this.
        // sense.AddSpaDiagnostic(node.Left!.Range, GSCErrorCodes.InvalidAssignmentTarget);
        return ScrData.Default;
    }

    private ScrData AnalyseCompoundAssignOp(BinaryExprNode node, SymbolTable symbolTable, TokenType op)
    {
        // Evaluate LHS without creating a RHS usage token, and RHS normally
        ScrData left = AnalyseExpr(node.Left!, symbolTable, Sense, false);
        ScrData right = AnalyseExpr(node.Right!, symbolTable, Sense);

        // For compound assignments on local variables, ensure the variable already exists
        if (node.Left is IdentifierExprNode identifier)
        {
            if (!symbolTable.ContainsSymbol(identifier.Identifier))
            {
                AddDiagnostic(identifier.Range, GSCErrorCodes.NotDefined, identifier.Identifier);
                return ScrData.Default;
            }
        }

        // Compute the result of the compound operation
        ScrData result = ExecuteCompoundOp(op, node, left, right);

        // Perform the assignment using the shared helper
        TryAssignToTarget(node.Left!, left, result, symbolTable);

        return result;
    }

    private ScrData ExecuteCompoundOp(TokenType op, BinaryExprNode node, ScrData left, ScrData right)
    {
        return op switch
        {
            TokenType.PlusAssign => AnalyseAddOp(node, left, right),
            TokenType.MinusAssign => AnalyseMinusOp(node, left, right),
            TokenType.MultiplyAssign => AnalyseMultiplyOp(node, left, right),
            TokenType.DivideAssign => AnalyseDivideOp(node, left, right),
            TokenType.ModuloAssign => AnalyseModuloOp(node, left, right),
            TokenType.BitAndAssign => AnalyseBitAndOp(node, left, right),
            TokenType.BitOrAssign => AnalyseBitOrOp(node, left, right),
            TokenType.BitXorAssign => AnalyseBitXorOp(node, left, right),
            TokenType.BitLeftShiftAssign => AnalyseBitLeftShiftOp(node, left, right),
            TokenType.BitRightShiftAssign => AnalyseBitRightShiftOp(node, left, right),
            _ => ScrData.Default,
        };
    }

    private ScrData AnalysePlusAssignOp(BinaryExprNode node, SymbolTable symbolTable)
    {
        return AnalyseCompoundAssignOp(node, symbolTable, TokenType.PlusAssign);
    }

    private ScrData AnalyseAddOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }

        // If both are numeric, we can add them together.
        if (left.IsNumeric() && right.IsNumeric())
        {
            if (left.Type == ScrDataTypes.Int && right.Type == ScrDataTypes.Int)
            {
                // Adds the values if they exist, otherwise is just null (ie Value Unknown)
                return new ScrData(ScrDataTypes.Int, left.GetIntegerValue() + right.GetIntegerValue());
            }

            return new ScrData(ScrDataTypes.Float, left.GetNumericValue() + right.GetNumericValue());
        }

        // If both are vectors, we can add them together.
        if (left.Type == ScrDataTypes.Vec3 && right.Type == ScrDataTypes.Vec3)
        {
            // TODO: add vec3d addition
            return new ScrData(ScrDataTypes.Vec3);
        }

        // At least one is a string, so do string concatenation. Won't be both numbers, as we checked that earlier.
        if (left.Type == ScrDataTypes.String || right.Type == ScrDataTypes.String || left.IsNumeric() || right.IsNumeric())
        {
            if (left.ValueUnknown() || right.ValueUnknown())
            {
                return new ScrData(ScrDataTypes.String);
            }

            return new ScrData(ScrDataTypes.String, left.Value!.ToString() + right.Value!.ToString());
        }

        // ERROR: Operator '+' cannot be applied on operands of type ...
        AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "+", left.TypeToString(), right.TypeToString());
        return ScrData.Default;
    }

    private ScrData AnalyseMinusOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }

        if (left.Type == ScrDataTypes.Int && right.Type == ScrDataTypes.Int)
        {
            return new ScrData(ScrDataTypes.Int, left.GetIntegerValue() - right.GetIntegerValue());
        }

        if (left.IsNumeric() && right.IsNumeric())
        {
            return new ScrData(ScrDataTypes.Float, left.GetNumericValue() - right.GetNumericValue());
        }

        if (left.Type == ScrDataTypes.Vec3 && right.Type == ScrDataTypes.Vec3)
        {
            // TODO: add vec3d subtraction
            return new ScrData(ScrDataTypes.Vec3);
        }

        // ERROR: Operator '-' cannot be applied on operands of type ...
        AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "-", left.TypeToString(), right.TypeToString());
        return ScrData.Default;
    }

    private ScrData AnalyseMultiplyOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }

        // TODO: double check if GSC even does integer multiplication, i'm not sure.
        if (left.Type == ScrDataTypes.Int && right.Type == ScrDataTypes.Int)
        {
            return new ScrData(ScrDataTypes.Int, left.GetIntegerValue() * right.GetIntegerValue());
        }

        if (left.IsNumeric() && right.IsNumeric())
        {
            return new ScrData(ScrDataTypes.Float, left.GetNumericValue() * right.GetNumericValue());
        }

        // TODO: not sure whether multiply can be done on vec3d, etc., need to check.
        if (left.Type == ScrDataTypes.Vec3 && right.IsNumeric())
        {
            // TODO: add vec3d multiplication by scalar value
            return new ScrData(ScrDataTypes.Vec3);
        }

        AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "*", left.TypeToString(), right.TypeToString());
        return ScrData.Default;
    }

    private ScrData AnalyseDivideOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }

        // don't think gsc does integer division
        // if (left.Type == ScrDataTypes.Int && right.Type == ScrDataTypes.Int)
        // {
        //     return new ScrData(ScrDataTypes.Int, left.Get<int?>() / right.Get<int?>());
        // }

        if (left.Type == ScrDataTypes.Vec3 && right.IsNumeric())
        {
            // TODO: add vec3d division by scalar value
            return new ScrData(ScrDataTypes.Vec3);
        }

        if (left.IsNumeric() && right.IsNumeric())
        {
            return new ScrData(ScrDataTypes.Float, left.GetNumericValue() / right.GetNumericValue());
        }

        AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "/", left.TypeToString(), right.TypeToString());
        return ScrData.Default;
    }

    private ScrData AnalyseModuloOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }

        if (left.Type == ScrDataTypes.Int && right.Type == ScrDataTypes.Int)
        {
            return new ScrData(ScrDataTypes.Int, left.GetIntegerValue() % right.GetIntegerValue());
        }

        AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "%", left.TypeToString(), right.TypeToString());
        return ScrData.Default;
    }

    private ScrData AnalyseBitLeftShiftOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }

        if (left.Type == ScrDataTypes.Int && right.Type == ScrDataTypes.Int)
        {
            return new ScrData(ScrDataTypes.Int, left.GetIntegerValue() << right.GetIntegerValue());
        }

        AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "<<", left.TypeToString(), right.TypeToString());
        return ScrData.Default;
    }

    private ScrData AnalyseBitRightShiftOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }

        if (left.Type == ScrDataTypes.Int && right.Type == ScrDataTypes.Int)
        {
            return new ScrData(ScrDataTypes.Int, left.GetIntegerValue() >> right.GetIntegerValue());
        }

        AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, ">>", left.TypeToString(), right.TypeToString());
        return ScrData.Default;
    }

    private ScrData AnalyseEqualsOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }

        if (left.ValueUnknown() || right.ValueUnknown())
        {
            return new ScrData(ScrDataTypes.Bool);
        }

        // Undefined can't be compared, that's what isdefined is for.
        if (left.Type == ScrDataTypes.Undefined || right.Type == ScrDataTypes.Undefined)
        {
            AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "==", left.TypeToString(), right.TypeToString());
            return ScrData.Default;
        }

        // Warn them if either side is possibly undefined.
        if (left.HasType(ScrDataTypes.Undefined))
        {
            AddDiagnostic(node.Left!.Range, GSCErrorCodes.PossibleUndefinedComparison);
            return new ScrData(ScrDataTypes.Bool);
        }
        if (right.HasType(ScrDataTypes.Undefined))
        {
            AddDiagnostic(node.Right!.Range, GSCErrorCodes.PossibleUndefinedComparison);
            return new ScrData(ScrDataTypes.Bool);
        }

        // TODO: this is a blunt instrument and I don't think it's correct
        return new ScrData(ScrDataTypes.Bool, left.Value == right.Value);
    }

    private ScrData AnalyseNotEqualsOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }

        if (left.ValueUnknown() || right.ValueUnknown())
        {
            return new ScrData(ScrDataTypes.Bool);
        }

        // Undefined can't be compared, that's what isdefined is for.
        if (left.Type == ScrDataTypes.Undefined || right.Type == ScrDataTypes.Undefined)
        {
            AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "!=", left.TypeToString(), right.TypeToString());
            return ScrData.Default;
        }

        // Warn them if either side is possibly undefined.
        if (left.HasType(ScrDataTypes.Undefined))
        {
            AddDiagnostic(node.Left!.Range, GSCErrorCodes.PossibleUndefinedComparison);
            return new ScrData(ScrDataTypes.Bool);
        }
        if (right.HasType(ScrDataTypes.Undefined))
        {
            AddDiagnostic(node.Right!.Range, GSCErrorCodes.PossibleUndefinedComparison);
            return new ScrData(ScrDataTypes.Bool);
        }

        // TODO: this is a blunt instrument and I don't think it's correct
        return new ScrData(ScrDataTypes.Bool, left.Value != right.Value);
    }

    private ScrData AnalyseIdentityEqualsOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }

        if (left.ValueUnknown() || right.ValueUnknown())
        {
            return new ScrData(ScrDataTypes.Bool);
        }

        // Undefined can't be compared, that's what isdefined is for.
        if (left.Type == ScrDataTypes.Undefined || right.Type == ScrDataTypes.Undefined)
        {
            AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "===", left.TypeToString(), right.TypeToString());
            return ScrData.Default;
        }

        // Warn them if either side is possibly undefined.
        if (left.HasType(ScrDataTypes.Undefined))
        {
            AddDiagnostic(node.Left!.Range, GSCErrorCodes.PossibleUndefinedComparison);
            return new ScrData(ScrDataTypes.Bool);
        }
        if (right.HasType(ScrDataTypes.Undefined))
        {
            AddDiagnostic(node.Right!.Range, GSCErrorCodes.PossibleUndefinedComparison);
            return new ScrData(ScrDataTypes.Bool);
        }

        // TODO: this is definitely not right.
        return new ScrData(ScrDataTypes.Bool, left.Value == right.Value && left.Type == right.Type);
    }

    private ScrData AnalyseIdentityNotEqualsOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }

        if (left.ValueUnknown() || right.ValueUnknown())
        {
            return new ScrData(ScrDataTypes.Bool);
        }

        // Undefined can't be compared, that's what isdefined is for.
        if (left.Type == ScrDataTypes.Undefined || right.Type == ScrDataTypes.Undefined)
        {
            AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "!==", left.TypeToString(), right.TypeToString());
            return ScrData.Default;
        }

        // Warn them if either side is possibly undefined.
        if (left.HasType(ScrDataTypes.Undefined))
        {
            AddDiagnostic(node.Left!.Range, GSCErrorCodes.PossibleUndefinedComparison);
            return new ScrData(ScrDataTypes.Bool);
        }
        if (right.HasType(ScrDataTypes.Undefined))
        {
            AddDiagnostic(node.Right!.Range, GSCErrorCodes.PossibleUndefinedComparison);
            return new ScrData(ScrDataTypes.Bool);
        }

        // TODO: this is definitely not right.
        return new ScrData(ScrDataTypes.Bool, left.Value != right.Value || left.Type != right.Type);
    }

    private ScrData AnalyseAndOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }

        if (left.ValueUnknown() || right.ValueUnknown())
        {
            return new ScrData(ScrDataTypes.Bool);
        }

        // Undefined can't be compared, that's what isdefined is for.
        if (left.Type == ScrDataTypes.Undefined || right.Type == ScrDataTypes.Undefined)
        {
            AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "&&", left.TypeToString(), right.TypeToString());
            return ScrData.Default;
        }

        // Warn them if either side is possibly undefined.
        if (left.HasType(ScrDataTypes.Undefined))
        {
            AddDiagnostic(node.Left!.Range, GSCErrorCodes.PossibleUndefinedComparison);
            return new ScrData(ScrDataTypes.Bool);
        }
        if (right.HasType(ScrDataTypes.Undefined))
        {
            AddDiagnostic(node.Right!.Range, GSCErrorCodes.PossibleUndefinedComparison);
            return new ScrData(ScrDataTypes.Bool);
        }

        // TODO: evaluate more intelligently - ie check if both are booleanish.
        return new ScrData(ScrDataTypes.Bool);
    }

    private ScrData AnalyseOrOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }
        if (left.ValueUnknown() || right.ValueUnknown())
        {
            return new ScrData(ScrDataTypes.Bool);
        }

        // Undefined can't be compared, that's what isdefined is for.
        if (left.Type == ScrDataTypes.Undefined || right.Type == ScrDataTypes.Undefined)
        {
            AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "||", left.TypeToString(), right.TypeToString());
            return ScrData.Default;
        }

        // Warn them if either side is possibly undefined.
        if (left.HasType(ScrDataTypes.Undefined))
        {
            AddDiagnostic(node.Left!.Range, GSCErrorCodes.PossibleUndefinedComparison);
            return new ScrData(ScrDataTypes.Bool);
        }
        if (right.HasType(ScrDataTypes.Undefined))
        {
            AddDiagnostic(node.Right!.Range, GSCErrorCodes.PossibleUndefinedComparison);
            return new ScrData(ScrDataTypes.Bool);
        }

        // TODO: evaluate more intelligently - ie check if both are booleanish.
        return new ScrData(ScrDataTypes.Bool);
    }

    private ScrData AnalyseBitAndOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }

        if (left.Type == ScrDataTypes.Int && right.Type == ScrDataTypes.Int)
        {
            return new ScrData(ScrDataTypes.Int, left.GetIntegerValue() & right.GetIntegerValue());
        }

        AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "&", left.TypeToString(), right.TypeToString());
        return ScrData.Default;
    }

    private ScrData AnalyseBitOrOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }

        if (left.Type == ScrDataTypes.Int && right.Type == ScrDataTypes.Int)
        {
            return new ScrData(ScrDataTypes.Int, left.GetIntegerValue() | right.GetIntegerValue());
        }

        AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "|", left.TypeToString(), right.TypeToString());
        return ScrData.Default;
    }

    private ScrData AnalyseBitXorOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }

        if (left.Type == ScrDataTypes.Int && right.Type == ScrDataTypes.Int)
        {
            return new ScrData(ScrDataTypes.Int, left.GetIntegerValue() ^ right.GetIntegerValue());
        }

        AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "^", left.TypeToString(), right.TypeToString());
        return ScrData.Default;
    }

    private ScrData AnalyseGreaterThanOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }

        if (left.ValueUnknown() || right.ValueUnknown())
        {
            // If we can't determine values but types are numeric, still return bool
            if (left.IsNumeric() && right.IsNumeric())
            {
                return new ScrData(ScrDataTypes.Bool);
            }
        }

        if (left.IsNumeric() && right.IsNumeric())
        {
            if (!left.ValueUnknown() && !right.ValueUnknown())
            {
                return new ScrData(ScrDataTypes.Bool, left.GetNumericValue() > right.GetNumericValue());
            }
            return new ScrData(ScrDataTypes.Bool);
        }

        AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, ">", left.TypeToString(), right.TypeToString());
        return ScrData.Default;
    }

    private ScrData AnalyseLessThanOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }

        if (left.ValueUnknown() || right.ValueUnknown())
        {
            // If we can't determine values but types are numeric, still return bool
            if (left.IsNumeric() && right.IsNumeric())
            {
                return new ScrData(ScrDataTypes.Bool);
            }
        }

        if (left.IsNumeric() && right.IsNumeric())
        {
            if (!left.ValueUnknown() && !right.ValueUnknown())
            {
                return new ScrData(ScrDataTypes.Bool, left.GetNumericValue() < right.GetNumericValue());
            }
            return new ScrData(ScrDataTypes.Bool);
        }

        AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "<", left.TypeToString(), right.TypeToString());
        return ScrData.Default;
    }

    private ScrData AnalyseGreaterThanEqualsOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }

        if (left.ValueUnknown() || right.ValueUnknown())
        {
            // If we can't determine values but types are numeric, still return bool
            if (left.IsNumeric() && right.IsNumeric())
            {
                return new ScrData(ScrDataTypes.Bool);
            }
        }

        if (left.IsNumeric() && right.IsNumeric())
        {
            if (!left.ValueUnknown() && !right.ValueUnknown())
            {
                return new ScrData(ScrDataTypes.Bool, left.GetNumericValue() >= right.GetNumericValue());
            }
            return new ScrData(ScrDataTypes.Bool);
        }

        AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, ">=", left.TypeToString(), right.TypeToString());
        return ScrData.Default;
    }

    private ScrData AnalyseLessThanEqualsOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }

        if (left.ValueUnknown() || right.ValueUnknown())
        {
            // If we can't determine values but types are numeric, still return bool
            if (left.IsNumeric() && right.IsNumeric())
            {
                return new ScrData(ScrDataTypes.Bool);
            }
        }

        if (left.IsNumeric() && right.IsNumeric())
        {
            if (!left.ValueUnknown() && !right.ValueUnknown())
            {
                return new ScrData(ScrDataTypes.Bool, left.GetNumericValue() <= right.GetNumericValue());
            }
            return new ScrData(ScrDataTypes.Bool);
        }

        AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "<=", left.TypeToString(), right.TypeToString());
        return ScrData.Default;
    }

    private ScrData AnalyseDotOp(BinaryExprNode node, SymbolTable symbolTable, bool createSenseTokenForField = true)
    {
        if (node.Right!.OperatorType != ExprOperatorType.IdentifierOperand || node.Right is not IdentifierExprNode identifierNode)
        {
            AddDiagnostic(node.Right!.Range, GSCErrorCodes.IdentifierExpected);
            return ScrData.Default;
        }

        ScrData left = AnalyseExpr(node.Left!, symbolTable, Sense);

        bool isClassMember = symbolTable.CurrentClass is not null &&
            symbolTable.CurrentClass.Members.Any(m => m.Name.Equals(identifierNode.Identifier, StringComparison.OrdinalIgnoreCase));

        if (left.TypeUnknown())
        {
            if (createSenseTokenForField)
            {
                if (isClassMember)
                    Sense.AddSenseToken(identifierNode.Token, new ScrClassPropertySymbol(identifierNode, ScrData.Default, symbolTable.CurrentClass!));
                else
                    Sense.AddSenseToken(identifierNode.Token, new ScrFieldSymbol(identifierNode, ScrData.Default));
            }
            return ScrData.Default;
        }

        if (left.Type == ScrDataTypes.Array && identifierNode.Identifier == "size")
        {
            ScrData sizeValue = new(ScrDataTypes.Int, ReadOnly: true);
            if (createSenseTokenForField)
                Sense.AddSenseToken(identifierNode.Token, new ScrFieldSymbol(identifierNode, sizeValue, true));
            return sizeValue;
        }

        if (left.Type != ScrDataTypes.Object && left.Type != ScrDataTypes.Struct && left.Type != ScrDataTypes.Entity)
        {
            AddDiagnostic(node.Range, GSCErrorCodes.DoesNotContainMember, identifierNode.Identifier, left.TypeToString());
            return ScrData.Default;
        }

        if (left.ValueUnknown())
        {
            if (createSenseTokenForField)
            {
                if (isClassMember)
                    Sense.AddSenseToken(identifierNode.Token, new ScrClassPropertySymbol(identifierNode, ScrData.Default, symbolTable.CurrentClass!));
                else
                    Sense.AddSenseToken(identifierNode.Token, new ScrFieldSymbol(identifierNode, ScrData.Default));
            }
            return ScrData.Default;
        }

        ScrData value = left.GetField(identifierNode.Identifier);

        if (createSenseTokenForField)
        {
            if (isClassMember)
                Sense.AddSenseToken(identifierNode.Token, new ScrClassPropertySymbol(identifierNode, value, symbolTable.CurrentClass!));
            else
                Sense.AddSenseToken(identifierNode.Token, new ScrFieldSymbol(identifierNode, value));
        }

        return value;
    }

    private ScrData AnalyseDataExpr(DataExprNode expr)
    {
        return new ScrData(expr.Type, expr.Value);
    }

    private ScrData AnalyseIdentifierExpr(IdentifierExprNode expr, SymbolTable symbolTable, bool createSenseTokenForRhs = true)
    {
        // Check if this identifier is a class member (before checking local variables)
        if (symbolTable.CurrentClass is not null)
        {
            bool isClassMember = symbolTable.CurrentClass.Members.Any(m => m.Name.Equals(expr.Identifier, StringComparison.OrdinalIgnoreCase));

            if (isClassMember)
            {
                // This is an implicit reference to a class member (like accessing this.member)
                if (createSenseTokenForRhs)
                {
                    Sense.AddSenseToken(expr.Token, new ScrClassPropertySymbol(expr, ScrData.Default, symbolTable.CurrentClass));
                }
                // Return a default type since we don't track member values without explicit self reference
                return ScrData.Default;
            }
        }

        // Analyze and return the corresponding ScrData for the local variable
        ScrData? value = symbolTable.TryGetLocalVariable(expr.Identifier, out SymbolFlags flags);
        if (value is not ScrData data)
        {
            return ScrData.Undefined();
        }

        if (data.Type != ScrDataTypes.Undefined)
        {
            if (flags.HasFlag(SymbolFlags.Global))
            {
                if (createSenseTokenForRhs)
                {
                    Sense.AddSenseToken(expr.Token, ScrVariableSymbol.LanguageSymbol(expr, data));
                }
                return data;
            }
            if (createSenseTokenForRhs)
            {
                Sense.AddSenseToken(expr.Token, ScrVariableSymbol.Usage(expr, data));
            }
        }
        return data;
    }

    private ScrData AnalyseVectorExpr(VectorExprNode expr, SymbolTable symbolTable)
    {
        if (expr.Y is null || expr.Z is null)
        {
            return ScrData.Default;
        }

        ScrData x = AnalyseExpr(expr.X, symbolTable, Sense);
        ScrData y = AnalyseExpr(expr.Y, symbolTable, Sense);
        ScrData z = AnalyseExpr(expr.Z, symbolTable, Sense);

        if (x.TypeUnknown() || y.TypeUnknown() || z.TypeUnknown() || x.ValueUnknown() || y.ValueUnknown() || z.ValueUnknown())
        {
            return new ScrData(ScrDataTypes.Vec3);
        }

        if (!x.IsNumeric())
        {
            AddDiagnostic(expr.X!.Range, GSCErrorCodes.InvalidVectorComponent, x.TypeToString());
            return ScrData.Default;
        }
        if (!y.IsNumeric())
        {
            AddDiagnostic(expr.Y!.Range, GSCErrorCodes.InvalidVectorComponent, y.TypeToString());
            return ScrData.Default;
        }
        if (!z.IsNumeric())
        {
            AddDiagnostic(expr.Z!.Range, GSCErrorCodes.InvalidVectorComponent, z.TypeToString());
            return ScrData.Default;
        }

        return new ScrData(ScrDataTypes.Vec3, new Vector3(x.GetNumericValue()!.Value, y.GetNumericValue()!.Value, z.GetNumericValue()!.Value));
    }

    private ScrData AnalyseIndexerExpr(ArrayIndexNode expr, SymbolTable symbolTable)
    {
        ScrData collection = AnalyseExpr(expr.Array, symbolTable, Sense);

        if (expr.Index is null)
        {
            return ScrData.Default;
        }

        ScrData indexer = AnalyseExpr(expr.Index, symbolTable, Sense);

        if (indexer.TypeUnknown())
        {
            return ScrData.Default;
        }

        // We might not know which collection type it is, but it won't be indexable.
        if (collection.TypeUnknown())
        {
            if (indexer.Type != ScrDataTypes.Int && indexer.Type != ScrDataTypes.String)
            {
                AddDiagnostic(expr.Index!.Range, GSCErrorCodes.CannotUseAsIndexer, indexer.TypeToString());
            }
            return ScrData.Default;
        }

        // TODO: I'm not sure 100% how GSC differentiates between array and map.
        // It might be that it implicitly converts an array to a map as soon as it first gets
        // a non-int index.
        // We'll do it like this for now, and adjust later if needed.

        if (collection.Type == ScrDataTypes.Array)
        {
            if (indexer.Type != ScrDataTypes.Int && indexer.Type != ScrDataTypes.String)
            {
                AddDiagnostic(expr.Index!.Range, GSCErrorCodes.CannotUseAsIndexer, indexer.TypeToString());
                return ScrData.Default;
            }

            // return collection.GetArrayElement(indexer.Get<int>());
            return ScrData.Default;
        }

        return ScrData.Default;
    }

    private ScrData AnalyseCallOnExpr(CalledOnNode expr, SymbolTable symbolTable)
    {
        ScrData target = AnalyseExpr(expr.On, symbolTable, Sense);

        return AnalyseCall(expr.Call, symbolTable, Sense, target);
    }

    private ScrData AnalyseCall(ExprNode call, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? target = null)
    {
        // If target is null, we don't know, just use any.
        ScrData targetValue = target ?? ScrData.Default;

        // Analyse the call.
        return call.OperatorType switch
        {
            ExprOperatorType.FunctionCall => AnalyseFunctionCall((FunCallNode)call, symbolTable, sense, targetValue),
            ExprOperatorType.Prefix when call is PrefixExprNode prefix && prefix.Operation == TokenType.Thread => AnalyseThreadedFunctionCall(prefix, symbolTable, sense, targetValue),
            ExprOperatorType.Binary when call is NamespacedMemberNode namespaced => AnalyseScopeResolution(namespaced, symbolTable, sense, targetValue),
            // for now... might be an error later.
            _ => ScrData.Default
        };
    }

    private ScrData AnalyseScopeResolution(NamespacedMemberNode namespaced, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? target = null)
    {
        ScrData targetValue = target ?? ScrData.Default;

        // I'm pretty sure the grammar stops this from happening, but no harm in being sure.
        if (namespaced.Namespace is not IdentifierExprNode namespaceNode)
        {
            AddDiagnostic(namespaced.Namespace.Range, GSCErrorCodes.IdentifierExpected);
            return ScrData.Default;
        }

        // Emit the namespace symbol.
        Sense.AddSenseToken(namespaceNode.Token, new ScrNamespaceScopeSymbol(namespaceNode));

        // Now find what symbol within the namespace we're targeting.
        // Again - probably not necessary, but no harm in being sure.
        if (namespaced.Member is not IdentifierExprNode memberNode)
        {
            AddDiagnostic(namespaced.Member!.Range, GSCErrorCodes.IdentifierExpected);
            return ScrData.Default;
        }

        // TODO: we're missing a check if the namespace exists. Incorporate (#24) here.

        ScrData symbol = symbolTable.TryGetNamespacedFunctionSymbol(namespaceNode.Identifier, memberNode.Identifier, out SymbolFlags flags);

        if (flags.HasFlag(SymbolFlags.Global) && symbol.Type == ScrDataTypes.Function)
        {
            ScrFunction function = symbol.Get<ScrFunction>();

            // Check if the namespace is a class
            if (symbolTable.GlobalSymbolTable.TryGetValue(namespaceNode.Identifier, out IExportedSymbol? classSymbol)
                && classSymbol.Type == ExportedSymbolType.Class)
            {
                ScrClass scrClass = (ScrClass)classSymbol;
                Sense.AddSenseToken(memberNode.Token, new ScrMethodReferenceSymbol(memberNode.Token, function, scrClass));
            }
            else
            {
                Sense.AddSenseToken(memberNode.Token, new ScrFunctionReferenceSymbol(memberNode.Token, function));
            }
        }

        return symbol;
    }

    /// <summary>
    /// Analyzes a dereference operation [[ expr ]], validating that expr is a FunctionPointer
    /// and converting it to a Function type for calling.
    /// </summary>
    /// <param name="derefExpr">The expression being dereferenced (inside [[ ]])</param>
    /// <param name="symbolTable">The symbol table for lookups</param>
    /// <param name="sense">IntelliSense for diagnostics</param>
    /// <returns>A Function if valid, or Default/Undefined otherwise</returns>
    private ScrData AnalyseDeref(ExprNode derefExpr, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        // Analyze the expression inside the dereference brackets
        ScrData functionPtrData = AnalyseExpr(derefExpr, symbolTable, sense);

        // If type is unknown, we can't validate
        if (functionPtrData.TypeUnknown())
        {
            return ScrData.Default;
        }

        // Validate that it's a FunctionPointer
        if (functionPtrData.Type != ScrDataTypes.FunctionPointer)
        {
            // Not a function pointer - emit diagnostic
            AddDiagnostic(derefExpr.Range, GSCErrorCodes.ExpectedFunction, functionPtrData.TypeToString());
            return ScrData.Default;
        }

        // Dereference: FunctionPointer → Function
        return new ScrData(ScrDataTypes.Function, functionPtrData.Value);
    }

    private ScrData AnalyseFunctionCall(FunCallNode call, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? target = null)
    {
        ScrData targetValue = target ?? ScrData.Default;

        // Get the function we're targeting.
        if (call.Function is null)
        {
            return ScrData.Default;
        }

        ScrData functionTarget;

        // If it's a direct identifier, look it up in the global function table
        // This handles: b() - looks up function b in globals
        if (call.Function is IdentifierExprNode identifierNode)
        {
            functionTarget = symbolTable.TryGetFunction(identifierNode.Identifier, out SymbolFlags flags);

            if (functionTarget.Type == ScrDataTypes.Undefined)
            {
                AddDiagnostic(call.Function.Range, GSCErrorCodes.FunctionDoesNotExist, identifierNode.Identifier);
                return ScrData.Default;
            }

            // Add sense token for the function reference
            if (!flags.HasFlag(SymbolFlags.Reserved) && !functionTarget.ValueUnknown())
            {
                AddFunctionReferenceToken(identifierNode.Token, functionTarget.Get<ScrFunction>(), symbolTable);
            }
        }
        // If it's a namespaced function, analyze it as scope resolution
        // This handles: namespace::func() - direct global function call
        else if (call.Function is NamespacedMemberNode namespacedMember)
        {
            functionTarget = AnalyseScopeResolution(namespacedMember, symbolTable, sense);
        }
        else
        {
            // For dereference calls [[ expr ]](), explicitly validate and dereference
            // This expects a FunctionPointer and returns a Function
            functionTarget = AnalyseDeref(call.Function, symbolTable, sense);
        }

        if (functionTarget.TypeUnknown())
        {
            return ScrData.Default;
        }

        if (functionTarget.Type != ScrDataTypes.Function)
        {
            AddDiagnostic(call.Function!.Range, GSCErrorCodes.ExpectedFunction, functionTarget.TypeToString());
            return ScrData.Default;
        }

        ScrFunction function = functionTarget.Get<ScrFunction>();

        // Analyse arguments
        foreach (ExprNode? argument in call.Arguments.Arguments)
        {
            if (argument is null)
            {
                continue;
            }

            ScrData argumentValue = AnalyseExpr(argument, symbolTable, sense);

            // TODO: Check whether argument types match expected parameter types
        }

        // Validate argument count
        int argCount = call.Arguments.Arguments.Count;
        string functionName = call.Function is IdentifierExprNode idNode ? idNode.Identifier :
                             call.Function is NamespacedMemberNode nmNode && nmNode.Member is IdentifierExprNode memberId ? memberId.Identifier :
                             function.Name;
        ValidateArgumentCount(function, argCount, call.Arguments.Range, functionName);

        return ScrData.Default;
    }

    private ScrData AnalyseThreadedFunctionCall(PrefixExprNode call, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? target = null)
    {
        ScrData targetValue = target ?? ScrData.Default;

        if (call.Operand is null)
        {
            return ScrData.Undefined();
        }

        ScrData functionTarget;
        FunCallNode? functionCall = null;

        // If it's a direct identifier, look it up in the global function table
        if (call.Operand is IdentifierExprNode identifierNode)
        {
            functionTarget = symbolTable.TryGetFunction(identifierNode.Identifier, out SymbolFlags flags);

            if (functionTarget.Type == ScrDataTypes.Undefined)
            {
                AddDiagnostic(call.Operand.Range, GSCErrorCodes.FunctionDoesNotExist, identifierNode.Identifier);
                return ScrData.Undefined();
            }

            // Add sense token for the function reference
            if (!flags.HasFlag(SymbolFlags.Reserved) && !functionTarget.ValueUnknown())
            {
                AddFunctionReferenceToken(identifierNode.Token, functionTarget.Get<ScrFunction>(), symbolTable);
            }
        }
        // If it's a namespaced function, analyze it as scope resolution
        // This handles: thread namespace::func() - direct global function call
        else if (call.Operand is NamespacedMemberNode namespacedMember)
        {
            functionTarget = AnalyseScopeResolution(namespacedMember, symbolTable, sense);
        }
        // If it's a function call node, analyze it and extract the function being called
        else if (call.Operand is FunCallNode funCall)
        {
            functionCall = funCall;
            // Recursively analyze the function call to get the target
            functionTarget = AnalyseFunctionCall(funCall, symbolTable, sense, targetValue);
        }
        else
        {
            // For dereference calls thread [[ expr ]](), explicitly validate and dereference
            functionTarget = AnalyseDeref(call.Operand, symbolTable, sense);
        }

        // Verify it's actually a function
        if (!functionTarget.TypeUnknown() && functionTarget.Type != ScrDataTypes.Function)
        {
            AddDiagnostic(call.Operand.Range, GSCErrorCodes.ExpectedFunction, functionTarget.TypeToString());
        }

        // TODO: Validate argument count for threaded calls (if not already handled by recursive call)

        // Threaded calls won't return anything.
        return ScrData.Undefined();
    }

    private void BuildClassContextMap(CfgNode start, ScrClass scrClass)
    {
        HashSet<CfgNode> visited = new();
        Stack<CfgNode> stack = new();
        stack.Push(start);

        while (stack.Count > 0)
        {
            CfgNode node = stack.Pop();
            if (!visited.Add(node)) continue;

            // When we encounter a function entry node (method), map it and all its descendants
            if (node.Type == CfgNodeType.FunctionEntry)
            {
                MapMethodNodes((FunEntryBlock)node, scrClass);
            }

            foreach (CfgNode successor in node.Outgoing)
            {
                stack.Push(successor);
            }
        }
    }

    private void MapMethodNodes(FunEntryBlock methodEntry, ScrClass scrClass)
    {
        // Map all nodes reachable from this method entry to the containing class
        HashSet<CfgNode> visited = new();
        Stack<CfgNode> stack = new();
        stack.Push(methodEntry);

        while (stack.Count > 0)
        {
            CfgNode node = stack.Pop();
            if (!visited.Add(node)) continue;

            // Map this node to the class
            NodeToClassMap[node] = scrClass;

            // Stop at function exit (don't continue beyond the method)
            if (node.Type == CfgNodeType.FunctionExit)
            {
                continue;
            }

            foreach (CfgNode successor in node.Outgoing)
            {
                stack.Push(successor);
            }
        }
    }

    public void AddDiagnostic(Range range, GSCErrorCodes code, params object[] args)
    {
        // Only issue the diagnostics on the final pass.
        if (Silent)
        {
            return;
        }
        Sense.AddSpaDiagnostic(range, code, args);
    }

    private static int CountAllNodes(ControlFlowGraph graph)
    {
        HashSet<CfgNode> visited = new();
        Stack<CfgNode> stack = new();
        stack.Push(graph.Start);

        while (stack.Count > 0)
        {
            CfgNode node = stack.Pop();
            if (visited.Add(node))
            {
                foreach (CfgNode successor in node.Outgoing)
                {
                    if (!visited.Contains(successor))
                    {
                        stack.Push(successor);
                    }
                }
            }
        }

        return visited.Count;
    }
}


file static class DataFlowAnalyserExtensions
{
    public static void MergeTables(this Dictionary<string, ScrVariable> target, Dictionary<string, ScrVariable> source, int maxScope)
    {
        try
        {
            // Get keys that are present in either
            HashSet<string> fields = new();

            fields.UnionWith(target.Keys);
            fields.UnionWith(source.Keys);

            foreach (string field in fields)
            {
                // Shouldn't carry over anything that's not higher than this in scope, it's not accessible
                if (source.TryGetValue(field, out ScrVariable? sourceData) && sourceData.LexicalScope <= maxScope)
                {
                    // Also present in target, and are different. Merge them
                    if (target.TryGetValue(field, out ScrVariable? targetData))
                    {
                        if (sourceData != targetData)
                        {
                            target[field] = new(sourceData.Name, ScrData.Merge(targetData.Data, sourceData.Data), sourceData.LexicalScope, sourceData.Global);
                        }
                        continue;
                    }

                    // Otherwise just copy one
                    target[field] = new(sourceData.Name, sourceData.Data.Copy(), sourceData.LexicalScope, sourceData.Global);
                }
            }
        }
        catch (StackOverflowException ex)
        {
            Log.Error(ex, "Stack overflow occurred while merging tables. Original target: {target}, source: {source}", target, source);
            throw;
        }
    }

    public static bool VariableTableEquals(this Dictionary<string, ScrVariable> target, Dictionary<string, ScrVariable> source)
    {
        if (target.Count != source.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, ScrVariable> pair in target)
        {
            if (!source.TryGetValue(pair.Key, out ScrVariable? value))
            {
                return false;
            }

            if (pair.Value != value)
            {
                return false;
            }
        }

        return true;
    }
}