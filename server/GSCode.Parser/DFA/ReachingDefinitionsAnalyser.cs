using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using GSCode.Data;
using GSCode.Data.Models;
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

internal ref partial struct ReachingDefinitionsAnalyser(List<Tuple<ScrFunction, ControlFlowGraph>> functionGraphs, List<Tuple<ScrClass, ControlFlowGraph>> classGraphs, ParserIntelliSense sense, Dictionary<string, IExportedSymbol> exportedSymbolTable, ScriptAnalyserData? apiData = null, string? currentNamespace = null, HashSet<string>? knownNamespaces = null)
{
    public List<Tuple<ScrFunction, ControlFlowGraph>> FunctionGraphs { get; } = functionGraphs;
    public List<Tuple<ScrClass, ControlFlowGraph>> ClassGraphs { get; } = classGraphs;
    public ParserIntelliSense Sense { get; } = sense;
    public Dictionary<string, IExportedSymbol> ExportedSymbolTable { get; } = exportedSymbolTable;
    public ScriptAnalyserData? ApiData { get; } = apiData;
    public string? CurrentNamespace { get; } = currentNamespace;
    public HashSet<string>? KnownNamespaces { get; } = knownNamespaces;

    public Dictionary<CfgNode, Dictionary<string, ScrVariable>> InSets { get; } = new();
    public Dictionary<CfgNode, Dictionary<string, ScrVariable>> OutSets { get; } = new();
    public Dictionary<(CfgNode From, CfgNode To), Dictionary<string, ScrVariable>> OutEdgeSets { get; } = new();

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
        Sense.SilentSenseTokens = true;

        // Clear state at the start of each function analysis
        SwitchContexts.Clear();
        InSets.Clear();
        OutSets.Clear();
        OutEdgeSets.Clear();

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
                // Prefer edge-specific OUT (needed for type narrowing), fall back to node OUT.
                if (OutEdgeSets.TryGetValue((incoming, node), out Dictionary<string, ScrVariable>? edgeOut))
                {
                    inSet.MergeTables(edgeOut, node.Scope);
                    continue;
                }
                if (OutSets.TryGetValue(incoming, out Dictionary<string, ScrVariable>? nodeOut))
                {
                    inSet.MergeTables(nodeOut, node.Scope);
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

            ConditionResult? decisionCondition = null;
            ConditionResult? iterationCondition = null;

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

                iterationCondition = AnalyseIterationInternal((IterationNode)node, symbolTable);
                OutSets[node] = symbolTable.VariableSymbols;
            }
            else if (node.Type == CfgNodeType.DecisionNode)
            {
                ScrClass? currentClass = NodeToClassMap.GetValueOrDefault(node);
                SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass);

                decisionCondition = AnalyseDecisionConditionInternal((DecisionNode)node, symbolTable);
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
                OutSets[node] = inSet;
            }

            // Update edge-specific out sets (used for branch-sensitive narrowing).
            bool edgeOutChanged = UpdateOutEdgeSetsForNode(node, OutSets[node],
                node.Type == CfgNodeType.DecisionNode ? decisionCondition : node.Type == CfgNodeType.IterationNode ? iterationCondition : null);

            // Check if the outset has changed before queueing successors.
            bool outSetChanged = previousOutSet == null || !previousOutSet.VariableTableEquals(OutSets[node]) || edgeOutChanged;

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
            // Only warn if we're very close to limit (>90%) or have an unusually high iteration ratio
            double iterationRatio = (double)iterations / Math.Max(1, totalNodes);
            bool likelyConvergenceIssue = iterationRatio > 80; // >80 iterations per node suggests issues

            if (likelyConvergenceIssue || iterations > maxIterations * 0.9)
            {
                Log.Warning("Reaching definitions analysis hit iteration limit ({MaxIterations}) for function {FunctionName} ({NodeCount} nodes, {Iterations} iterations, {IterationRatio:F1}x per node). This may indicate convergence issues.",
                    maxIterations, function.Name ?? "<anonymous>", totalNodes, iterations, iterationRatio);
            }
        }

        // Now that analysis is done, do one final pass to add diagnostics and sense tokens.
        Silent = false;
        Sense.SilentSenseTokens = false;

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
                    AnalyseBasicBlock((BasicBlock)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass, CurrentNamespace, KnownNamespaces));
                    break;
                case CfgNodeType.ClassMembersBlock:
                    AnalyseClassMembersBlock((ClassMembersBlock)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass, CurrentNamespace, KnownNamespaces));
                    break;
                case CfgNodeType.EnumerationNode:
                    AnalyseEnumeration((EnumerationNode)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass, CurrentNamespace, KnownNamespaces));
                    break;
                case CfgNodeType.IterationNode:
                    AnalyseIteration((IterationNode)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass, CurrentNamespace, KnownNamespaces));
                    break;
                case CfgNodeType.SwitchNode:
                    AnalyseSwitch((SwitchNode)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass, CurrentNamespace, KnownNamespaces));
                    break;
                case CfgNodeType.SwitchCaseDecisionNode:
                    AnalyseSwitchCaseDecision((SwitchCaseDecisionNode)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass, CurrentNamespace, KnownNamespaces));
                    break;
                case CfgNodeType.DecisionNode:
                    AnalyseDecision((DecisionNode)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass, CurrentNamespace, KnownNamespaces));
                    break;
            }
        }
    }

    public void AnalyseClass(ScrClass scrClass, ControlFlowGraph classGraph)
    {
        Silent = true;
        Sense.SilentSenseTokens = true;

        // Clear state at the start of each class analysis
        SwitchContexts.Clear();
        InSets.Clear();
        OutSets.Clear();
        OutEdgeSets.Clear();

        // Build a map of all function entry nodes to this class
        BuildClassContextMap(classGraph.Start, scrClass);

        Stack<CfgNode> worklist = new();
        worklist.Push(classGraph.Start);

        HashSet<CfgNode> visited = new();

        // Calculate iteration limit based on graph size to prevent infinite loops
        int totalNodes = CountAllNodes(classGraph);
        int maxIterations = Math.Max(100, totalNodes * 20); // At least 100, or 20x nodes
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
                // Prefer edge-specific OUT (needed for type narrowing), fall back to node OUT.
                if (OutEdgeSets.TryGetValue((incoming, node), out Dictionary<string, ScrVariable>? edgeOut))
                {
                    inSet.MergeTables(edgeOut, node.Scope);
                    continue;
                }
                if (OutSets.TryGetValue(incoming, out Dictionary<string, ScrVariable>? nodeOut))
                {
                    inSet.MergeTables(nodeOut, node.Scope);
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

            ConditionResult? decisionCondition = null;
            ConditionResult? iterationCondition = null;

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

                iterationCondition = AnalyseIterationInternal((IterationNode)node, symbolTable);
                OutSets[node] = symbolTable.VariableSymbols;
            }
            else if (node.Type == CfgNodeType.DecisionNode)
            {
                ScrClass? currentClass = NodeToClassMap.GetValueOrDefault(node);
                SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass);

                decisionCondition = AnalyseDecisionConditionInternal((DecisionNode)node, symbolTable);
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
                OutSets[node] = inSet;
            }

            // Update edge-specific out sets (used for branch-sensitive narrowing).
            bool edgeOutChanged = UpdateOutEdgeSetsForNode(node, OutSets[node],
                node.Type == CfgNodeType.DecisionNode ? decisionCondition : node.Type == CfgNodeType.IterationNode ? iterationCondition : null);

            // Check if the outset has changed before queueing successors.
            bool outSetChanged = previousOutSet == null || !previousOutSet.VariableTableEquals(OutSets[node]) || edgeOutChanged;

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
            Log.Warning("Reaching definitions analysis hit iteration limit ({MaxIterations}) for class {ClassName} ({NodeCount} nodes, {Iterations} iterations). This may indicate convergence issues.",
                maxIterations, scrClass.Name ?? "<anonymous>", totalNodes, iterations);
        }

        // Now that analysis is done, do one final pass to add diagnostics and sense tokens.
        Silent = false;
        Sense.SilentSenseTokens = false;

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
                    AnalyseBasicBlock((BasicBlock)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass, CurrentNamespace, KnownNamespaces));
                    break;
                case CfgNodeType.ClassMembersBlock:
                    AnalyseClassMembersBlock((ClassMembersBlock)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass, CurrentNamespace, KnownNamespaces));
                    break;
                case CfgNodeType.EnumerationNode:
                    AnalyseEnumeration((EnumerationNode)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass, CurrentNamespace, KnownNamespaces));
                    break;
                case CfgNodeType.IterationNode:
                    AnalyseIteration((IterationNode)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass, CurrentNamespace, KnownNamespaces));
                    break;
                case CfgNodeType.SwitchNode:
                    AnalyseSwitch((SwitchNode)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass, CurrentNamespace, KnownNamespaces));
                    break;
                case CfgNodeType.SwitchCaseDecisionNode:
                    AnalyseSwitchCaseDecision((SwitchCaseDecisionNode)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass, CurrentNamespace, KnownNamespaces));
                    break;
                case CfgNodeType.DecisionNode:
                    AnalyseDecision((DecisionNode)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass, CurrentNamespace, KnownNamespaces));
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

    private void ValidateArgumentCount(ScrFunction? function, int argCount, Range callRange, string functionName, SymbolFlags flags)
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

        // Determine if this is an unverified built-in (autogenerated from Treyarch's API)
        bool isUnverifiedBuiltIn = flags.HasFlag(SymbolFlags.BuiltIn) &&
                                   function.Flags.Contains("autogenerated", StringComparer.OrdinalIgnoreCase);

        // Track the minimum required args across all overloads and maximum allowed args
        int globalMinArgs = int.MaxValue;
        int globalMaxArgs = 0;
        bool anyOverloadHasVararg = false;

        // Check all overloads to see if any match the argument count
        foreach (ScrFunctionOverload overload in function.Overloads)
        {
            // Parameters can be null in some cases (e.g., API functions)
            if (overload?.Parameters is null)
            {
                return; // Can't validate if any overload is missing parameter info
            }

            int minArgs = overload.Parameters.Count(p => p.Mandatory == true);
            int maxArgs = overload.Parameters.Count;
            bool hasVararg = overload.Vararg;

            // Track global bounds
            if (minArgs < globalMinArgs)
            {
                globalMinArgs = minArgs;
            }
            if (maxArgs > globalMaxArgs)
            {
                globalMaxArgs = maxArgs;
            }
            if (hasVararg)
            {
                anyOverloadHasVararg = true;
            }

            // Check if this overload accepts the argument count
            if (hasVararg)
            {
                // Vararg accepts minArgs or more
                if (argCount >= minArgs)
                {
                    return; // Valid for this overload
                }
            }
            else
            {
                // Regular overload: check bounds
                if (argCount >= minArgs && argCount <= maxArgs)
                {
                    return; // Valid for this overload
                }
            }
        }

        // No overload matched - emit diagnostic
        // NOTE: "Too few arguments" is only checked for built-in functions. GSC treats all
        // parameters as optional (missing args become undefined), so we can't reliably validate
        // user-defined functions without a better way to determine required parameters.
        bool isBuiltIn = flags.HasFlag(SymbolFlags.BuiltIn);

        if (isBuiltIn && argCount < globalMinArgs)
        {
            // Too few arguments - report the minimum required (built-ins only)
            if (isUnverifiedBuiltIn)
            {
                AddDiagnostic(callRange, GSCErrorCodes.TooFewArgumentsUnverified, functionName, argCount, globalMinArgs);
            }
            else
            {
                AddDiagnostic(callRange, GSCErrorCodes.TooFewArguments, functionName, argCount, globalMinArgs);
            }
        }
        else if (isBuiltIn && !anyOverloadHasVararg && argCount > globalMaxArgs)
        {
            // Too many arguments - report the maximum allowed (only if no vararg, built-ins only)
            if (isUnverifiedBuiltIn)
            {
                AddDiagnostic(callRange, GSCErrorCodes.TooManyArgumentsUnverified, functionName, argCount, globalMaxArgs);
            }
            else
            {
                AddDiagnostic(callRange, GSCErrorCodes.TooManyArguments, functionName, argCount, globalMaxArgs);
            }
        }
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
            AssignmentResult keyAssignmentResult = symbolTable.AddOrSetVariableSymbol(keyIdentifier.Lexeme, ScrData.Default, definitionSource: foreachStmt);

            if (keyAssignmentResult == AssignmentResult.SuccessNew)
            {
                Sense.AddSenseToken(keyIdentifier, ScrVariableSymbol.Declaration(foreachStmt.KeyIdentifier, ScrData.Default));
            }
        }

        Token valueIdentifier = foreachStmt.ValueIdentifier.Token;
        AssignmentResult valueAssignmentResult = symbolTable.AddOrSetVariableSymbol(valueIdentifier.Lexeme, ScrData.Default, definitionSource: foreachStmt);

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
        _ = AnalyseIterationInternal(node, symbolTable);
    }

    public void AnalyseDecision(DecisionNode node, SymbolTable symbolTable)
    {
        _ = AnalyseDecisionConditionInternal(node, symbolTable);
    }

    private ConditionResult? AnalyseIterationInternal(IterationNode node, SymbolTable symbolTable)
    {
        // Analyse the initialisation.
        if (node.Initialisation is not null)
        {
            AnalyseExpr(node.Initialisation, symbolTable, Sense);

            // For loop initialization should follow the same rules as expression statements
            // Only assignments, calls, increments, decrements should be allowed
            ValidateExpressionHasSideEffects(node.Initialisation);
        }

        ConditionResult? conditionResult = null;

        // Analyse the condition if present
        if (node.Condition is not null)
        {
            conditionResult = AnalyseCondition(node.Condition, symbolTable);
            ScrData conditionValue = conditionResult.Value.Value;

            if (!conditionValue.TypeUnknown() && !conditionValue.CanEvaluateToBoolean())
            {
                AddDiagnostic(node.Condition.Range, GSCErrorCodes.NoImplicitConversionExists, conditionValue.TypeToString(), ScrDataTypeNames.Bool);
            }
        }

        // Analyse the increment if present
        if (node.Increment is not null)
        {
            AnalyseExpr(node.Increment, symbolTable, Sense);

            // For loop increment should also follow expression statement rules
            ValidateExpressionHasSideEffects(node.Increment);
        }

        return conditionResult;
    }

    private ConditionResult? AnalyseDecisionConditionInternal(DecisionNode node, SymbolTable symbolTable)
    {
        DecisionAstNode decision = node.Source;

        // It either errored or it's an else, nothing to do.
        if (decision.Condition is null)
        {
            return null;
        }

        ConditionResult conditionResult = AnalyseCondition(decision.Condition, symbolTable);
        ScrData conditionValue = conditionResult.Value;

        if (!conditionValue.TypeUnknown() && !conditionValue.CanEvaluateToBoolean())
        {
            AddDiagnostic(decision.Condition.Range, GSCErrorCodes.NoImplicitConversionExists, conditionValue.TypeToString(), ScrDataTypeNames.Bool);
        }

        // TODO: if the condition evaluates to false, then mark the block that follows as unreachable.
        // TODO: if the condition evaluates to true, then any else blocks are unreachable.
        return conditionResult;
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
        AssignmentResult assignmentResult = symbolTable.TryAddVariableSymbol(memberName, ScrData.Default, definitionSource: memberDecl);

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

        // Validate that the RHS is a compile-time constant
        if (!IsConstantExpression(statement.Value))
        {
            AddDiagnostic(statement.Value!.Range, GSCErrorCodes.ExpectedConstantExpression);
            return;
        }

        // Assign the result to the symbol table.
        AssignmentResult assignmentResult = symbolTable.TryAddVariableSymbol(statement.Identifier, result, isConstant: true, sourceLocation: statement.IdentifierToken.Range, definitionSource: statement);

        if (assignmentResult == AssignmentResult.SuccessNew || assignmentResult == AssignmentResult.SuccessAlreadyDefined)
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
                duration.TypeToString(), ScrDataTypeNames.Number);
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
            ExprOperatorType.WaittillMatch => AnalyseWaittillMatchExpr((WaittillMatchNode)expr, symbolTable, sense),
            ExprOperatorType.Ternary => AnalyseTernaryExpr((TernaryExprNode)expr, symbolTable, sense),
            ExprOperatorType.MethodCall => AnalyseMethodCall((MethodCallNode)expr, symbolTable, sense),
            ExprOperatorType.Deref => AnalyseDeref((DerefExprNode)expr, symbolTable, sense),
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
        foreach (IdentifierExprNode? variable in expr.Variables.Variables)
        {
            if (variable is null)
            {
                continue; // Skip ignored parameters (undefined)
            }

            symbolTable.AddOrSetVariableSymbol(variable.Identifier, ScrData.Default, definitionSource: expr);
            Sense.AddSenseToken(variable.Token, ScrVariableSymbol.Declaration(variable, ScrData.Default));
        }

        // Waittill doesn't return.
        return ScrData.Void;
    }

    private ScrData AnalyseWaittillMatchExpr(WaittillMatchNode expr, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        ScrData notifyName = AnalyseExpr(expr.NotifyName, symbolTable, sense);
        ScrData entity = AnalyseExpr(expr.Entity, symbolTable, sense);

        // The called-on must be an entity.
        if (entity.Type != ScrDataTypes.Entity && !entity.IsAny())
        {
            AddDiagnostic(expr.Entity.Range, GSCErrorCodes.NoImplicitConversionExists, entity.TypeToString(), ScrDataTypeNames.Entity);
            return ScrData.Default;
        }

        // The notify name must be a string or hash.
        if (notifyName.Type != ScrDataTypes.String && notifyName.Type != ScrDataTypes.Hash && !notifyName.IsAny())
        {
            AddDiagnostic(expr.NotifyName.Range, GSCErrorCodes.NoImplicitConversionExists, notifyName.TypeToString(), ScrDataTypeNames.String, ScrDataTypeNames.Hash);
            return ScrData.Default;
        }

        // Analyse optional match value (must be string if provided).
        if (expr.MatchValue is not null)
        {
            ScrData matchValue = AnalyseExpr(expr.MatchValue, symbolTable, sense);
            if (matchValue.Type != ScrDataTypes.String && matchValue.Type != ScrDataTypes.Hash && !matchValue.IsAny())
            {
                AddDiagnostic(expr.MatchValue.Range, GSCErrorCodes.NoImplicitConversionExists, matchValue.TypeToString(), ScrDataTypeNames.String, ScrDataTypeNames.Hash);
                return ScrData.Default;
            }
        }

        // WaittillMatch doesn't return.
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
            return AnalyseDotOp(binary, symbolTable, createSenseTokenForRhs, out _);
        }

        if (TryAnalyseAssignmentBinaryExpr(binary, symbolTable, out ScrData assignmentResult))
        {
            return assignmentResult;
        }

        if (IsLogicalBinaryOperator(binary.Operation))
        {
            // NOTE: For now this is behavior-preserving (still analyzes both sides eagerly).
            // This is the hook point for short-circuit evaluation + type narrowing:
            // - AnalyseCondition(...) will return (value, factsWhenTrue, factsWhenFalse)
            // - && / || will analyse RHS under an env refined by LHS facts (reachability-aware)
            // - callers like if/while will apply the returned facts to the then/else environments
            return AnalyseLogicalBinaryExpr(binary, symbolTable);
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
    }

    private bool TryAnalyseAssignmentBinaryExpr(BinaryExprNode binary, SymbolTable symbolTable, out ScrData result)
    {
        switch (binary.Operation)
        {
            case TokenType.Assign:
                result = AnalyseAssignOp(binary, symbolTable);
                return true;
            case TokenType.PlusAssign:
                result = AnalysePlusAssignOp(binary, symbolTable);
                return true;
            case TokenType.MinusAssign:
            case TokenType.MultiplyAssign:
            case TokenType.DivideAssign:
            case TokenType.ModuloAssign:
            case TokenType.BitAndAssign:
            case TokenType.BitOrAssign:
            case TokenType.BitXorAssign:
            case TokenType.BitLeftShiftAssign:
            case TokenType.BitRightShiftAssign:
                result = AnalyseCompoundAssignOp(binary, symbolTable, binary.Operation);
                return true;
            default:
                result = default;
                return false;
        }
    }

    private ScrData AnalysePrefixExpr(PrefixExprNode prefix, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        return prefix.Operation switch
        {
            TokenType.Thread => AnalyseThreadedFunctionCall(prefix, symbolTable, sense),
            TokenType.BitAnd => AnalyseFunctionPointer(prefix, symbolTable, sense),
            TokenType.Not => AnalyseNotOp(prefix, symbolTable, sense),
            TokenType.Minus => AnalyseNegationOp(prefix, symbolTable, sense),
            TokenType.BitNot => AnalyseBitwiseNegationOp(prefix, symbolTable, sense),
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
        if (!flags.HasFlag(SymbolFlags.Reserved) && functionData.TryGetFunction(out var func))
        {
            AddFunctionReferenceToken(identifier.Token, func, symbolTable);
            // Return as a FunctionPointer type (a pointer to the function, not the function itself)
            return ScrData.FunctionPointer(func);
        }

        // Return as a FunctionPointer type (a pointer to the function, not the function itself)
        return new ScrData(ScrDataTypes.FunctionPointer);
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

        // Must be a number.
        if (!operand.IsNumeric())
        {
            AddDiagnostic(prefix.Range, GSCErrorCodes.NoImplicitConversionExists,
                operand.TypeToString(), ScrDataTypeNames.Int, ScrDataTypeNames.Float);
            return ScrData.Default;
        }

        if (operand.Type == ScrDataTypes.Int)
        {
            return new ScrData(ScrDataTypes.Int, booleanValue: operand.BooleanValue);
        }

        // If it's not int, then it's a float.
        return new ScrData(ScrDataTypes.Float);
    }

    private ScrData AnalyseBitwiseNegationOp(PrefixExprNode prefix, SymbolTable symbolTable, ParserIntelliSense sense)
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

        if (operand.Type != ScrDataTypes.Int)
        {
            AddDiagnostic(prefix.Range, GSCErrorCodes.NoImplicitConversionExists,
                operand.TypeToString(), ScrDataTypeNames.Int);
            return ScrData.Default;
        }

        return new ScrData(ScrDataTypes.Int, booleanValue: null);
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

            // Check if the symbol is a constant (tracked via symbol table, not ScrData.ReadOnly)
            if (symbolTable.SymbolIsConstant(symbolName))
            {
                AddDiagnostic(identifier.Range, GSCErrorCodes.CannotAssignToConstant, symbolName);
                return false;
            }

            symbolTable.SetSymbol(symbolName, newValue);
            Sense.AddSenseToken(identifier.Token, ScrVariableSymbol.Usage(identifier, newValue));
            return true;
        }

        // Assigning to a property on a struct/entity/object - use AnalyseDotOp to get the owner
        if (operand is BinaryExprNode binaryExprNode && binaryExprNode.Operation == TokenType.Dot)
        {
            ScrData dotResult = AnalyseDotOp(binaryExprNode, symbolTable, false, out ScrData owner);
            string fieldName = dotResult.FieldName ?? throw new NullReferenceException("Sanity check failed: Dot result has no field name.");

            if (!TryAssignField(binaryExprNode, owner, fieldName, newValue))
            {
                return false;
            }

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

    private bool TryAssignField(BinaryExprNode dotExpr, ScrData owner, string fieldName, ScrData value)
    {
        bool success = owner.TrySetField(fieldName, value, out ScrSetFieldFailure? failure);
        if (success)
        {
            return true;
        }

        if (failure is null)
        {
            AddDiagnostic(dotExpr.Range, GSCErrorCodes.InvalidAssignmentTarget);
            return false;
        }

        if (failure.Value.IncompatibleBaseTypes is ScrDataTypes incompatibleBaseTypes)
        {
            AddDiagnostic(dotExpr.Range, GSCErrorCodes.DoesNotContainMember, fieldName, ScrDataTypeNames.TypeToString(incompatibleBaseTypes));
            return false;
        }

        // Check for array size readonly first (it can exist alongside entity failures)
        bool anyReadOnly = failure.Value.ArraySizeReadOnly;

        if (failure.Value.EntityFailures is { } entityFailures)
        {
            // Single pass: collect flags + any extra info we may want for the chosen diagnostic.
            // We intentionally emit ONE diagnostic (highest priority) to avoid spam and repeated passes.
            bool anyImmutable = false;
            bool anyTypeMismatch = false;
            HashSet<ScrEntityTypes>? immutableEntityTypes = null;
            ScrDataTypes expectedForMismatch = ScrDataTypes.Void;

            foreach (ScrEntitySetFieldFailureInfo fail in entityFailures)
            {
                switch (fail.Reason)
                {
                    case ScrEntitySetFieldResult.EntityImmutable:
                        anyImmutable = true;
                        immutableEntityTypes ??= new HashSet<ScrEntityTypes>();
                        immutableEntityTypes.Add(fail.EntityType);
                        break;
                    case ScrEntitySetFieldResult.FieldReadOnly:
                        anyReadOnly = true;
                        break;
                    case ScrEntitySetFieldResult.FieldTypeMismatch:
                        anyTypeMismatch = true;
                        expectedForMismatch |= ScrEntityRegistry.GetField(fail.EntityType, fieldName).Type;
                        break;
                }
            }

            // Prefer a single, high-signal diagnostic.
            // Priority (most actionable first):
            // 1) Immutable entity (can't ever work)
            // 2) Readonly property (can't ever work)
            // 3) Type mismatch (might work with cast/change)
            if (anyImmutable)
            {
                string immutableTypes = immutableEntityTypes is null || immutableEntityTypes.Count == 0
                    ? ScrDataTypeNames.Entity
                    : string.Join(" | ", immutableEntityTypes.Select(ScrEntityTypeNames.TypeToString));

                AddDiagnostic(dotExpr.Range, GSCErrorCodes.CannotAssignToImmutableEntity, immutableTypes);
                return false;
            }

            if (anyReadOnly)
            {
                AddDiagnostic(dotExpr.Right!.Range, GSCErrorCodes.CannotAssignToReadOnlyProperty, fieldName);
                return false;
            }

            if (anyTypeMismatch)
            {
                string expectedType = expectedForMismatch == ScrDataTypes.Void
                    ? ScrDataTypeNames.Any
                    : ScrDataTypeNames.TypeToString(expectedForMismatch);

                AddDiagnostic(dotExpr.Right!.Range, GSCErrorCodes.PredefinedFieldTypeMismatch, value.TypeToString(), expectedType);
                return false;
            }
        }
        // Handle array size readonly when there are no entity failures
        else if (anyReadOnly)
        {
            AddDiagnostic(dotExpr.Right!.Range, GSCErrorCodes.CannotAssignToReadOnlyProperty, fieldName);
            return false;
        }

        AddDiagnostic(dotExpr.Range, GSCErrorCodes.InvalidAssignmentTarget);
        return false;
    }

    private ScrData AnalysePostIncrementOp(PostfixExprNode postfix, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        ScrData target = AnalyseExpr(postfix.Operand!, symbolTable, sense, false);

        // Must be an int.
        if (target.Type != ScrDataTypes.Int && !target.IsAny())
        {
            AddDiagnostic(postfix.Operand!.Range, GSCErrorCodes.NoImplicitConversionExists, target.TypeToString(), ScrDataTypeNames.Int);
            return ScrData.Default;
        }

        // Perform the assignment using the shared helper
        if (!TryAssignToTarget(postfix.Operand!, target, new ScrData(target.Type), symbolTable))
        {
            return ScrData.Default;
        }

        // Return its old value (post-increment returns the value before incrementing)
        return target;
    }

    private ScrData AnalysePostDecrementOp(PostfixExprNode postfix, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        ScrData target = AnalyseExpr(postfix.Operand!, symbolTable, sense, false);

        // Must be an int.
        if (target.Type != ScrDataTypes.Int && !target.IsAny())
        {
            AddDiagnostic(postfix.Operand!.Range, GSCErrorCodes.NoImplicitConversionExists, target.TypeToString(), ScrDataTypeNames.Int);
            return ScrData.Default;
        }

        // Perform the assignment using the shared helper
        if (!TryAssignToTarget(postfix.Operand!, target, new ScrData(target.Type), symbolTable))
        {
            return ScrData.Default;
        }

        // Return its old value (post-decrement returns the value before decrementing)
        return target;
    }

    private ScrData AnalyseAssignOp(BinaryExprNode node, SymbolTable symbolTable)
    {
        ScrData right = AnalyseExpr(node.Right!, symbolTable, Sense);

        // If left is a dot expression, analyse it directly so we can get trace of the owner.
        if (node.Left is BinaryExprNode binaryExprNode && binaryExprNode.Operation == TokenType.Dot)
        {
            ScrData dotLeft = AnalyseDotOp(binaryExprNode, symbolTable, false, out ScrData leftOwner);
            string fieldName = dotLeft.FieldName ?? throw new NullReferenceException("Sanity check failed: Dot result has no field name.");

            if (!TryAssignField(binaryExprNode, leftOwner, fieldName, right))
            {
                return ScrData.Default;
            }

            // Success - emit the sense token.
            IdentifierExprNode identifierNode = (IdentifierExprNode)binaryExprNode.Right!;
            Sense.AddSenseToken(identifierNode.Token, new ScrFieldSymbol(identifierNode, right));

            return right;
        }

        // Otherwise, any other type of assignment.
        ScrData left = AnalyseExpr(node.Left!, symbolTable, Sense, false);

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

            // Check if the symbol is a constant (tracked via symbol table, not ScrData.ReadOnly)
            if (symbolTable.SymbolIsConstant(symbolName))
            {
                AddDiagnostic(identifier.Range, GSCErrorCodes.CannotAssignToConstant, symbolName);
                return ScrData.Default;
            }

            if (right.Type == ScrDataTypes.Function)
            {
                AddDiagnostic(node.Right!.Range, GSCErrorCodes.StoreFunctionAsPointer);
                return ScrData.Default;
            }

            AssignmentResult assignmentResult = symbolTable.AddOrSetVariableSymbol(symbolName, right with { ReadOnly = false }, definitionSource: node);

            if (right.Type == ScrDataTypes.Undefined)
            {
                return right;
            }

            // Failed, because the symbol is a constant
            if (assignmentResult == AssignmentResult.FailedConstant)
            {
                AddDiagnostic(identifier.Range, GSCErrorCodes.CannotAssignToConstant, symbolName);
                return ScrData.Default;
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

        // NOTE: Dot expressions are handled at the top of this method via AnalyseDotOp

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
        if (!TryAssignToTarget(node.Left!, left, result, symbolTable))
        {
            return ScrData.Default;
        }

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
        ScrDataTypes addOpMask = ScrDataTypes.Number | ScrDataTypes.Vector | ScrDataTypes.String | ScrDataTypes.Hash;
        if((left.Type & addOpMask) == ScrDataTypes.Void || (right.Type & addOpMask) == ScrDataTypes.Void)
        {
            AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "+", left.TypeToString(), right.TypeToString());
            return ScrData.Default;
        }

        // If both are numeric, we can add them together.
        if (left.IsNumeric() && right.IsNumeric())
        {
            if (left.Type == ScrDataTypes.Int && right.Type == ScrDataTypes.Int)
            {
                return new ScrData(ScrDataTypes.Int);
            }

            return new ScrData(ScrDataTypes.Float);
        }

        // If both are vectors, we can add them together.
        if (left.Type == ScrDataTypes.Vector && right.Type == ScrDataTypes.Vector)
        {
            // TODO: add vec3d addition
            return new ScrData(ScrDataTypes.Vector);
        }

        ScrDataTypes vectorOrNumericMask = ScrDataTypes.Vector | ScrDataTypes.Number;
        // At least one is a string, so do string concatenation. Won't be both numbers, as we checked that earlier.
        if (left.Type == ScrDataTypes.String || right.Type == ScrDataTypes.String || (left.Type & vectorOrNumericMask) == vectorOrNumericMask || (right.Type & vectorOrNumericMask) == vectorOrNumericMask)
        {
            return new ScrData(ScrDataTypes.String);
        }

        // If one or both are hashes, we can add them together.
        if (left.Type == ScrDataTypes.Hash || right.Type == ScrDataTypes.Hash)
        {
            // But the other must be a string if they aren't both hashes.
            if (left.Type == ScrDataTypes.Hash && right.Type == ScrDataTypes.String)
            {
                return new ScrData(ScrDataTypes.Hash);
            }
            if (right.Type == ScrDataTypes.Hash && left.Type == ScrDataTypes.String)
            {
                return new ScrData(ScrDataTypes.Hash);
            }
            AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "+", left.TypeToString(), right.TypeToString());
            return ScrData.Default;
        }

        // Some union of types, but we won't compute it here for the moment. TODO change
        return ScrData.Default;
    }

    private ScrData AnalyseMinusOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }

        if(TryHandleNumericBinaryOperation(left, right, out ScrData? result))
        {
            return result!.Value;
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

        if(TryHandleNumericBinaryOperation(left, right, out ScrData? result))
        {
            return result!.Value;
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

        
        ScrDataTypes numericMask = ScrDataTypes.Number | ScrDataTypes.Vector;
        if((left.Type & numericMask) == ScrDataTypes.Void || (right.Type & numericMask) == ScrDataTypes.Void)
        {
            AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "/", left.TypeToString(), right.TypeToString());
            return ScrData.Default;
        }

        // Both are numeric, so result is a float.
        if (left.IsNumeric() && right.IsNumeric())
        {
            // If the right isn't truthy, then this is an attempted divide by zero.
            if(right.BooleanValue == false)
            {
                AddDiagnostic(node.Range, GSCErrorCodes.DivisionByZero);
                return ScrData.Default;
            }
            return new ScrData(ScrDataTypes.Float);
        }

        // If left OR right is a vector, and the other is numeric, then they cast upward to vector.
        if ((left.Type == ScrDataTypes.Vector || right.Type == ScrDataTypes.Vector) && (left.IsNumeric() || right.IsNumeric()))
        {
            return new ScrData(ScrDataTypes.Vector);
        }

        // There's some union of types, but we won't compute it here for the moment. TODO change
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
            // If the right isn't truthy, then this is an attempted divide by zero.
            if (right.BooleanValue == false)
            {
                AddDiagnostic(node.Range, GSCErrorCodes.DivisionByZero);
                return ScrData.Default;
            }
            return new ScrData(ScrDataTypes.Int);
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
            return new ScrData(ScrDataTypes.Int);
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
            return new ScrData(ScrDataTypes.Int);
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

        if (left.BooleanValue is null || right.BooleanValue is null)
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
        return new ScrData(ScrDataTypes.Bool, booleanValue: left.BooleanValue == right.BooleanValue);
    }

    private ScrData AnalyseNotEqualsOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }

        if (left.BooleanValue is null || right.BooleanValue is null)
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
        return new ScrData(ScrDataTypes.Bool, booleanValue: left.BooleanValue != right.BooleanValue);
    }

    private ScrData AnalyseIdentityEqualsOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }

        if (left.BooleanValue is null || right.BooleanValue is null)
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
        return new ScrData(ScrDataTypes.Bool, booleanValue: left.BooleanValue == right.BooleanValue && left.Type == right.Type);
    }

    private ScrData AnalyseIdentityNotEqualsOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }

        if (left.BooleanValue is null || right.BooleanValue is null)
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
        return new ScrData(ScrDataTypes.Bool, booleanValue: left.BooleanValue != right.BooleanValue || left.Type != right.Type);
    }

    private ScrData AnalyseAndOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }

        if (left.BooleanValue is null || right.BooleanValue is null)
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
        return new ScrData(ScrDataTypes.Bool, booleanValue: left.BooleanValue == right.BooleanValue);
    }

    private ScrData AnalyseOrOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }
        if (left.BooleanValue is null || right.BooleanValue is null)
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
            return new ScrData(ScrDataTypes.Int);
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
            return new ScrData(ScrDataTypes.Int);
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
            return new ScrData(ScrDataTypes.Int);
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

        if (left.IsNumeric() && right.IsNumeric())
        {
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

        if (left.IsNumeric() && right.IsNumeric())
        {
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

        if (left.IsNumeric() && right.IsNumeric())
        {
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

        if (left.IsNumeric() && right.IsNumeric())
        {
            return new ScrData(ScrDataTypes.Bool);
        }

        AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "<=", left.TypeToString(), right.TypeToString());
        return ScrData.Default;
    }

    private ScrData AnalyseDotOp(BinaryExprNode node, SymbolTable symbolTable, bool createSenseTokenForField, out ScrData owner)
    {
        owner = ScrData.Default;

        if (node.Right!.OperatorType != ExprOperatorType.IdentifierOperand || node.Right is not IdentifierExprNode identifierNode)
        {

            AddDiagnostic(node.Right!.Range, GSCErrorCodes.IdentifierExpected);
            return ScrData.Default;
        }

        ScrData left = AnalyseExpr(node.Left!, symbolTable, Sense);
        owner = left;

        ScrData result = left.TryGetField(identifierNode.Identifier, out ScrDataTypes? incompatibleTypes);

        if(result == ScrData.Void)
        {
            AddDiagnostic(node.Range, GSCErrorCodes.DoesNotContainMember, identifierNode.Identifier, ScrDataTypeNames.TypeToString(incompatibleTypes!.Value));
            // FieldName is already set by TryGetField, return a default with it preserved
            ScrData errorResult = ScrData.Default;
            errorResult.FieldName = identifierNode.Identifier;
            return errorResult;
        }

        bool isClassMember = symbolTable.CurrentClass is not null &&
            symbolTable.CurrentClass.Members.Any(m => m.Name.Equals(identifierNode.Identifier, StringComparison.OrdinalIgnoreCase));

        // Emit sense tokens for the field.
        if (createSenseTokenForField)
        {
            if (isClassMember)
            {
                Sense.AddSenseToken(identifierNode.Token, new ScrClassPropertySymbol(identifierNode, result, symbolTable.CurrentClass!, isReadOnly: result.ReadOnly));
            }
            else
            {
                Sense.AddSenseToken(identifierNode.Token, new ScrFieldSymbol(identifierNode, result, isReadOnly: result.ReadOnly));
            }
        }

        return result;
    }

    private ScrData AnalyseDataExpr(DataExprNode expr)
    {
        return ScrData.FromDataExprNode(expr);
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
                bool isConstant = symbolTable.SymbolIsConstant(expr.Identifier);
                Sense.AddSenseToken(expr.Token, ScrVariableSymbol.Usage(expr, data, isConstant));
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

        if (x.TypeUnknown() || y.TypeUnknown() || z.TypeUnknown())
        {
            return new ScrData(ScrDataTypes.Vector);
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

        return new ScrData(ScrDataTypes.Vector);
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
            if (!indexer.IsArrayIndexer())
            {
                AddDiagnostic(expr.Index!.Range, GSCErrorCodes.CannotUseAsIndexer, indexer.TypeToString());
            }
            return ScrData.Default;
        }

        // Arrays aren't strongly typed (right now), so we can just return default.
        if (collection.Type == ScrDataTypes.Array)
        {
            if (!indexer.IsArrayIndexer())
            {
                AddDiagnostic(expr.Index!.Range, GSCErrorCodes.CannotUseAsIndexer, indexer.TypeToString());
                return ScrData.Default;
            }

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
        return AnalyseScopeResolution(namespaced, symbolTable, sense, out _, target);
    }

    private ScrData AnalyseScopeResolution(NamespacedMemberNode namespaced, SymbolTable symbolTable, ParserIntelliSense sense, out SymbolFlags outFlags, ScrData? target = null)
    {
        outFlags = SymbolFlags.None;
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

        ScrData symbol = symbolTable.TryGetNamespacedFunctionSymbol(namespaceNode.Identifier, memberNode.Identifier, out SymbolFlags flags, out bool namespaceExists);
        outFlags = flags;

        // Validate that the namespace exists
        if (!namespaceExists)
        {
            AddDiagnostic(namespaceNode.Range, GSCErrorCodes.UnknownNamespace, namespaceNode.Identifier);
            return ScrData.Default;
        }

        if (flags.HasFlag(SymbolFlags.Global) && symbol.Type == ScrDataTypes.Function)
        {
            if (symbol.TryGetFunction(out ScrFunction? function))
            {
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
    private ScrData AnalyseDeref(DerefExprNode deref, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        // Analyze the expression inside the dereference brackets
        ScrData functionPtrData = AnalyseExpr(deref.Inner, symbolTable, sense);

        // If type is unknown, we can't validate
        if (functionPtrData.TypeUnknown())
        {
            return ScrData.Default;
        }

        // Validate that it's a FunctionPointer
        if (functionPtrData.Type != ScrDataTypes.FunctionPointer)
        {
            // Not a function pointer - emit diagnostic
            AddDiagnostic(deref.Inner.Range, GSCErrorCodes.ExpectedFunction, functionPtrData.TypeToString());
            return ScrData.Default;
        }

        // Dereference: FunctionPointer → Function
        if (functionPtrData.TryGetFunction(out var func))
        {
            return ScrData.Function(func);
        }
        return new ScrData(ScrDataTypes.Function);
    }

    private ScrData? TryAnalyseReservedFunction(FunCallNode call, string functionName, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        // Handle vectorscale(vector, number) -> vector
        if (functionName.Equals("vectorscale", StringComparison.OrdinalIgnoreCase))
        {
            return AnalyseVectorScaleCall(call, symbolTable, sense);
        }

        // Handle isdefined(value) -> bool
        if (functionName.Equals("isdefined", StringComparison.OrdinalIgnoreCase))
        {
            return AnalyseIsDefinedCall(call, symbolTable, sense);
        }

        return null; // Not a specially handled reserved function
    }

    private ScrData AnalyseVectorScaleCall(FunCallNode call, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        int argCount = call.Arguments.Arguments.Count;

        // Validate argument count (exactly 2)
        if (argCount < 2)
        {
            AddDiagnostic(call.Arguments.Range, GSCErrorCodes.TooFewArguments, "vectorscale", argCount, 2);
        }
        else if (argCount > 2)
        {
            AddDiagnostic(call.Arguments.Range, GSCErrorCodes.TooManyArguments, "vectorscale", argCount, 2);
        }

        int index = 0;
        foreach (ExprNode? arg in call.Arguments.Arguments)
        {
            if (arg is null)
            {
                index++;
                continue;
            }

            // Analyze first argument (should be Vector)
            if (index == 0)
            {
                ScrData vecData = AnalyseExpr(arg, symbolTable, sense);
                if (!vecData.TypeUnknown() && vecData.Type != ScrDataTypes.Vector)
                {
                    AddDiagnostic(arg.Range, GSCErrorCodes.NoImplicitConversionExists, vecData.TypeToString(), ScrDataTypeNames.Vector);
                }
            }
            // Analyze second argument (should be Int or Float / Number)
            else if (index == 1)
            {
                ScrData scaleData = AnalyseExpr(arg, symbolTable, sense);
                if (!scaleData.TypeUnknown() && !scaleData.IsNumeric())
                {
                    AddDiagnostic(arg.Range, GSCErrorCodes.NoImplicitConversionExists, scaleData.TypeToString(), ScrDataTypeNames.Number);
                }
            }
            // Analyze remaining arguments to ensure they are processed for side effects/references
            else
            {
                AnalyseExpr(arg, symbolTable, sense);
            }

            index++;
        }

        return new ScrData(ScrDataTypes.Vector);
    }

    private ScrData AnalyseIsDefinedCall(FunCallNode call, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        int argCount = call.Arguments.Arguments.Count;

        // Validate argument count (exactly 1)
        if (argCount < 1)
        {
            AddDiagnostic(call.Arguments.Range, GSCErrorCodes.TooFewArguments, "isdefined", argCount, 1);
        }
        else if (argCount > 1)
        {
            AddDiagnostic(call.Arguments.Range, GSCErrorCodes.TooManyArguments, "isdefined", argCount, 1);
        }

        // Analyze all arguments to ensure side effects/references are processed
        foreach (var arg in call.Arguments.Arguments)
        {
            if (arg != null)
            {
                AnalyseExpr(arg, symbolTable, sense);
            }
        }

        // isdefined takes 1 argument. Return bool.
        return new ScrData(ScrDataTypes.Bool);
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
        SymbolFlags functionFlags = SymbolFlags.None;

        // Dereference call [[ expr ]]() - look up the inner expression as a local variable
        if (call.Function is DerefExprNode derefNode)
        {
            functionTarget = AnalyseDeref(derefNode, symbolTable, sense);
            functionFlags = SymbolFlags.None;
        }
        // Direct identifier call foo() - look up in global function table
        else if (call.Function is IdentifierExprNode identifierNode)
        {
            // Pass argument count to enable signature-based fallback to API functions
            int argumentCount = call.Arguments?.Arguments?.Count ?? 0;
            functionTarget = symbolTable.TryGetFunction(identifierNode.Identifier, out functionFlags, argumentCount);

            if (functionTarget.Type == ScrDataTypes.Undefined)
            {
                AddDiagnostic(call.Function.Range, GSCErrorCodes.FunctionDoesNotExist, identifierNode.Identifier);
                return ScrData.Default;
            }

            // Add sense token for the function reference
            if (!functionFlags.HasFlag(SymbolFlags.Reserved) && functionTarget.TryGetFunction(out var func))
            {
                AddFunctionReferenceToken(identifierNode.Token, func, symbolTable);
            }
        }
        // Namespaced function call namespace::func() - analyze as scope resolution
        else if (call.Function is NamespacedMemberNode namespacedMember)
        {
            functionTarget = AnalyseScopeResolution(namespacedMember, symbolTable, sense, out functionFlags);
        }
        else
        {
            // Other complex expressions - analyze directly
            functionTarget = AnalyseExpr(call.Function, symbolTable, sense);
            functionFlags = SymbolFlags.None;
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

        functionTarget.TryGetFunction(out ScrFunction? function);

        // Handle reserved functions with special semantics
        if (functionFlags.HasFlag(SymbolFlags.Reserved) && call.Function is IdentifierExprNode reservedId)
        {
            ScrData? reservedResult = TryAnalyseReservedFunction(call, reservedId.Identifier, symbolTable, sense);
            if (reservedResult is not null)
            {
                return reservedResult.Value;
            }
        }

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
                             function?.Name ?? "<unknown>";
        ValidateArgumentCount(function, argCount, call.Arguments.Range, functionName, functionFlags);

        // Return the function's return type if known
        return GetFunctionReturnType(function);
    }

    /// <summary>
    /// Gets the return type of a function from its API specification.
    /// </summary>
    private static ScrData GetFunctionReturnType(ScrFunction? function)
    {
        if (function is null)
        {
            return ScrData.Default;
        }

        // Get the first overload's return type (TODO: handle multiple overloads)
        if (function.Overloads.Count == 0)
        {
            return ScrData.Default;
        }

        ScrFunctionReturn? returnSpec = function.Overloads[0].Returns;

        // If explicitly void, return void
        if (returnSpec?.Void == true)
        {
            return ScrData.Void;
        }

        // Convert the API return type to ScrData
        return ScrData.FromApiType(returnSpec?.Type);
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
            if (!flags.HasFlag(SymbolFlags.Reserved) && functionTarget.TryGetFunction(out var func))
            {
                AddFunctionReferenceToken(identifierNode.Token, func, symbolTable);
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
        // Dereference call thread [[ expr ]]() - look up the inner expression as a local variable
        else if (call.Operand is DerefExprNode derefNode)
        {
            functionTarget = AnalyseDeref(derefNode, symbolTable, sense);
        }
        else
        {
            // Other complex expressions - analyze directly
            functionTarget = AnalyseExpr(call.Operand, symbolTable, sense);
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

    private bool UpdateOutEdgeSetsForNode(CfgNode node, Dictionary<string, ScrVariable> baseOut, ConditionResult? condition)
    {
        bool anyChanged = false;

        foreach (CfgNode successor in node.Outgoing)
        {
            Dictionary<string, ScrVariable> edgeOut;

            if (condition is not null && node is DecisionNode decisionNode)
            {
                if (successor == decisionNode.WhenTrue)
                {
                    edgeOut = RefineVariableSymbols(baseOut, condition.Value.WhenTrue);
                }
                else if (successor == decisionNode.WhenFalse)
                {
                    edgeOut = RefineVariableSymbols(baseOut, condition.Value.WhenFalse);
                }
                else
                {
                    edgeOut = CloneVariableSymbols(baseOut);
                }
            }
            else if (condition is not null && node is IterationNode iterationNode)
            {
                if (successor == iterationNode.Body)
                {
                    edgeOut = RefineVariableSymbols(baseOut, condition.Value.WhenTrue);
                }
                else if (successor == iterationNode.Continuation)
                {
                    edgeOut = RefineVariableSymbols(baseOut, condition.Value.WhenFalse);
                }
                else
                {
                    edgeOut = CloneVariableSymbols(baseOut);
                }
            }
            else
            {
                edgeOut = CloneVariableSymbols(baseOut);
            }

            (CfgNode From, CfgNode To) edge = (node, successor);

            if (OutEdgeSets.TryGetValue(edge, out Dictionary<string, ScrVariable>? previousEdgeOut))
            {
                if (!previousEdgeOut.VariableTableEquals(edgeOut))
                {
                    anyChanged = true;
                }
            }
            else
            {
                anyChanged = true;
            }

            OutEdgeSets[edge] = edgeOut;
        }

        return anyChanged;
    }

    private static Dictionary<string, ScrVariable> CloneVariableSymbols(Dictionary<string, ScrVariable> symbols)
    {
        return new Dictionary<string, ScrVariable>(symbols, StringComparer.OrdinalIgnoreCase);
    }

    private Dictionary<string, ScrVariable> RefineVariableSymbols(Dictionary<string, ScrVariable> baseOut, Dictionary<string, TypeNarrowing> narrowings)
    {
        if (narrowings.Count == 0)
        {
            return CloneVariableSymbols(baseOut);
        }

        Dictionary<string, ScrVariable> refined = CloneVariableSymbols(baseOut);

        foreach ((string symbol, TypeNarrowing narrowing) in narrowings)
        {
            if (!refined.TryGetValue(symbol, out ScrVariable? existing) || existing is null)
            {
                continue;
            }

            ScrDataTypes newType = Apply(narrowing, existing.Data.Type);
            refined[symbol] = existing with { Data = existing.Data with { Type = newType } };
        }

        return refined;
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

    /// <summary>
    /// Determines if an expression is a compile-time constant that can be used in a const declaration.
    /// This includes literal values and expressions that can be evaluated at compile time.
    /// </summary>
    /// <param name="expr">The expression to check</param>
    /// <returns>True if the expression is a compile-time constant, false otherwise</returns>
    private static bool IsConstantExpression(ExprNode? expr)
    {
        if (expr == null)
            return false;

        return expr.OperatorType switch
        {
            // Literal values are always compile-time constants
            ExprOperatorType.DataOperand => true,

            // Binary expressions can be constants if both operands are constants
            ExprOperatorType.Binary when expr is BinaryExprNode binaryExpr => 
                IsCompileTimeConstantBinaryOp(binaryExpr.Operation) &&
                IsConstantExpression(binaryExpr.Left) && 
                IsConstantExpression(binaryExpr.Right),

            // Unary expressions can be constants if the operand is constant
            ExprOperatorType.Prefix when expr is PrefixExprNode prefixExpr =>
                IsCompileTimeConstantPrefixOp(prefixExpr.Operation) &&
                IsConstantExpression(prefixExpr.Operand),

            // Ternary expressions can be constants if all parts are constants
            ExprOperatorType.Ternary when expr is TernaryExprNode ternaryExpr =>
                IsConstantExpression(ternaryExpr.Condition) &&
                IsConstantExpression(ternaryExpr.Then) &&
                IsConstantExpression(ternaryExpr.Else),

            // Vector expressions can be constants if all components are constants
            ExprOperatorType.Vector when expr is VectorExprNode vectorExpr =>
                IsConstantExpression(vectorExpr.X) &&
                IsConstantExpression(vectorExpr.Y) &&
                IsConstantExpression(vectorExpr.Z),

            // All other expression types involve runtime evaluation
            _ => false
        };
    }

    /// <summary>
    /// Determines if a binary operator can be evaluated at compile time.
    /// </summary>
    private static bool IsCompileTimeConstantBinaryOp(TokenType operation)
    {
        return operation switch
        {
            // Arithmetic operators
            TokenType.Plus => true,
            TokenType.Minus => true,
            TokenType.Multiply => true,
            TokenType.Divide => true,
            TokenType.Modulo => true,
            
            // Bitwise operators
            TokenType.BitAnd => true,
            TokenType.BitOr => true,
            TokenType.BitXor => true,
            TokenType.BitLeftShift => true,
            TokenType.BitRightShift => true,
            
            // Logical operators
            TokenType.And => true,
            TokenType.Or => true,
            
            // Comparison operators
            TokenType.Equals => true,
            TokenType.NotEquals => true,
            TokenType.LessThan => true,
            TokenType.LessThanEquals => true,
            TokenType.GreaterThan => true,
            TokenType.GreaterThanEquals => true,
            
            // Assignment and other runtime operators are not compile-time constants
            _ => false
        };
    }

    /// <summary>
    /// Determines if a prefix operator can be evaluated at compile time.
    /// </summary>
    private static bool IsCompileTimeConstantPrefixOp(TokenType operation)
    {
        return operation switch
        {
            // Unary arithmetic operators
            TokenType.Plus => true,
            TokenType.Minus => true,
            TokenType.BitNot => true,
            TokenType.Not => true,
            
            // Runtime operators like increment/decrement are not compile-time constants
            _ => false
        };
    }

    private static bool TryHandleNumericBinaryOperation(ScrData left, ScrData right, [NotNullWhen(true)] out ScrData? result)
    {
        ScrDataTypes numericMask = ScrDataTypes.Number | ScrDataTypes.Vector;
        if((left.Type & numericMask) == ScrDataTypes.Void || (right.Type & numericMask) == ScrDataTypes.Void)
        {
            result = null;
            return false;
        }

        // Both are ints, so result is an int.
        if (left.Type == ScrDataTypes.Int && right.Type == ScrDataTypes.Int)
        {
            result = new ScrData(ScrDataTypes.Int);
            return true;
        }

        // At least one is a float, both numeric, so result is a float.
        if (left.IsNumeric() && right.IsNumeric())
        {
            result = new ScrData(ScrDataTypes.Float);
            return true;
        }

        // If left OR right is a vector, and the other is numeric, then they cast upward to vector.
        if ((left.Type == ScrDataTypes.Vector || right.Type == ScrDataTypes.Vector) && (left.IsNumeric() || right.IsNumeric()))
        {
            result = new ScrData(ScrDataTypes.Vector);
            return true;
        }

        // There's some union of types, but we won't compute it here for the moment. TODO change
        result = ScrData.Default;
        return true;
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
                            target[field] = sourceData with
                            {
                                Data = ScrData.Merge(targetData.Data, sourceData.Data),
                                IsConstant = sourceData.IsConstant && targetData.IsConstant,
                                SourceLocation = sourceData.SourceLocation ?? targetData.SourceLocation
                            };
                        }
                        continue;
                    }

                    // Otherwise just copy one
                    target[field] = sourceData with { Data = sourceData.Data.Copy() };
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