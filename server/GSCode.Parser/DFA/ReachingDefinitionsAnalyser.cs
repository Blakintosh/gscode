using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.CFA;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.SPA;
using GSCode.Parser.SPA.Logic.Components;
using Serilog;

namespace GSCode.Parser.DFA;

internal ref struct ReachingDefinitionsAnalyser(List<Tuple<ScrFunction, ControlFlowGraph>> functionGraphs, ParserIntelliSense sense, Dictionary<string, IExportedSymbol> exportedSymbolTable)
{
    public List<Tuple<ScrFunction, ControlFlowGraph>> FunctionGraphs { get; } = functionGraphs;
    public ParserIntelliSense Sense { get; } = sense;
    public Dictionary<string, IExportedSymbol> ExportedSymbolTable { get; } = exportedSymbolTable;

    public Dictionary<CfgNode, Dictionary<string, ScrVariable>> InSets { get; } = new();
    public Dictionary<CfgNode, Dictionary<string, ScrVariable>> OutSets { get; } = new();

    public bool Silent { get; set; } = true;

    public void Run()
    {
        foreach (Tuple<ScrFunction, ControlFlowGraph> functionGraph in FunctionGraphs)
        {
            AnalyseFunction(functionGraph.Item1, functionGraph.Item2);
        }
    }

    public void AnalyseFunction(ScrFunction function, ControlFlowGraph functionGraph)
    {
        Silent = true;

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
            else if (node.Type == CfgNodeType.BasicBlock)
            {
                SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope);

                // TODO: Unioning of sets is not ideal, better to merge the ScrDatas of common key across multiple dictionaries. Easier to use with the symbol tables.
                // TODO: Analyse statement-by-statement, using the analysers already created, and get the out set.
                //Analyse(node, symbolTable, inSets, outSets, Sense);
                //outSet.UnionWith(symbolTable.GetOutgoingSymbols());
                AnalyseBasicBlock((BasicBlock)node, symbolTable);

                OutSets[node] = symbolTable.VariableSymbols;
            }
            else if (node.Type == CfgNodeType.EnumerationNode)
            {
                SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope);

                AnalyseEnumeration((EnumerationNode)node, symbolTable);
                OutSets[node] = symbolTable.VariableSymbols;
            }
            else if (node.Type == CfgNodeType.IterationNode)
            {
                SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope);

                AnalyseIteration((IterationNode)node, symbolTable);
                OutSets[node] = symbolTable.VariableSymbols;
            }
            else if (node.Type == CfgNodeType.DecisionNode)
            {
                SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope);

                AnalyseDecision((DecisionNode)node, symbolTable);
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
            switch (node.Type)
            {
                case CfgNodeType.BasicBlock:
                    AnalyseBasicBlock((BasicBlock)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope));
                    break;
                case CfgNodeType.EnumerationNode:
                    AnalyseEnumeration((EnumerationNode)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope));
                    break;
                case CfgNodeType.IterationNode:
                    AnalyseIteration((IterationNode)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope));
                    break;
                case CfgNodeType.DecisionNode:
                    AnalyseDecision((DecisionNode)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope));
                    break;
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
        FunDefnNode node = entry.Source;

        // Add the function's parameters to the in set.
        foreach (ParamNode param in node.Parameters.Parameters)
        {
            if (param.Name is null)
            {
                continue;
            }

            inSet[param.Name.Lexeme] = new(param.Name.Lexeme, ScrData.Default, 0, false);
        }

        // Add global scoped variables to the in set.
        inSet["self"] = new("self", new ScrData(ScrDataTypes.Entity, ScrStruct.NonDeterministic()), 0, true);
        inSet["level"] = new("level", new ScrData(ScrDataTypes.Entity, ScrStruct.NonDeterministic()), 0, true);
        inSet["game"] = new("game", new ScrData(ScrDataTypes.Array), 0, true);
        inSet["anim"] = new("anim", new ScrData(ScrDataTypes.Entity, ScrStruct.NonDeterministic()), 0, true);

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

        Token valueIdentifier = foreachStmt.ValueIdentifier.Token;
        AssignmentResult assignmentResult = symbolTable.AddOrSetVariableSymbol(valueIdentifier.Lexeme, ScrData.Default);

        // if (!assignmentResult)
        // {
        //     // TODO: how does GSC handle this?
        // }
        if (assignmentResult == AssignmentResult.SuccessNew)
        {
            Sense.AddSenseToken(valueIdentifier, ScrVariableSymbol.Declaration(foreachStmt.ValueIdentifier, ScrData.Default));
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
            default:
                break;
        }
        ;
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

    private ScrData AnalyseExpr(ExprNode expr, SymbolTable symbolTable, ParserIntelliSense sense, bool createSenseTokenForRhs = true)
    {
        return expr.OperatorType switch
        {
            ExprOperatorType.Binary when expr is NamespacedMemberNode namespaceMember => AnalyseScopeResolution(namespaceMember, symbolTable, sense),
            ExprOperatorType.Binary => AnalyseBinaryExpr((BinaryExprNode)expr, symbolTable, createSenseTokenForRhs),
            ExprOperatorType.Prefix => AnalysePrefixExpr((PrefixExprNode)expr, symbolTable, sense),
            ExprOperatorType.DataOperand => AnalyseDataExpr((DataExprNode)expr),
            ExprOperatorType.IdentifierOperand => AnalyseIdentifierExpr((IdentifierExprNode)expr, symbolTable, createSenseTokenForRhs),
            ExprOperatorType.Vector => AnalyseVectorExpr((VectorExprNode)expr, symbolTable),
            ExprOperatorType.Indexer => AnalyseIndexerExpr((ArrayIndexNode)expr, symbolTable),
            ExprOperatorType.CallOn => AnalyseCallOnExpr((CalledOnNode)expr, symbolTable),
            ExprOperatorType.FunctionCall => AnalyseFunctionCall((FunCallNode)expr, symbolTable, sense),
            // ExprOperatorType.MethodCall => AnalyseMethodCall((MethodCallNode)expr, symbolTable, sense),
            _ => ScrData.Default,
        };
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
            TokenType.Equals => AnalyseEqualsOp(binary, left, right),
            TokenType.NotEquals => AnalyseNotEqualsOp(binary, left, right),
            _ => ScrData.Default,
        };
    }

    private ScrData AnalysePrefixExpr(PrefixExprNode prefix, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        return prefix.Operation switch
        {
            TokenType.Thread => AnalyseThreadedFunctionCall(prefix, symbolTable, sense),
            _ => ScrData.Default,
        };
    }

    private ScrData AnalyseAssignOp(BinaryExprNode node, SymbolTable symbolTable)
    {
        // TODO: decouple this from the general binary handler, then when it's an identifier, manually resolve either it to be a declaration
        // or a mutation. Check for const too.
        // actually, might not be necessary. undefined doesn't highlight, so the isNew check should suffice.
        ScrData left = AnalyseExpr(node.Left!, symbolTable, Sense, false);
        ScrData right = AnalyseExpr(node.Right!, symbolTable, Sense);

        // Assigning to a local variable
        if (node.Left is IdentifierExprNode identifier)
        {
            string symbolName = identifier.Identifier;

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

            bool isNew = destination.Set(fieldName, right);

            // TODO: decide whether to add definition modifier
            if (binaryExprNode.Right is IdentifierExprNode identifierNode)
            {
                Sense.AddSenseToken(identifierNode.Token, new ScrFieldSymbol(identifierNode, right));
            }

            return right;
        }

        // TODO: once all cases are covered, we should enable this.
        // sense.AddSpaDiagnostic(node.Left!.Range, GSCErrorCodes.InvalidAssignmentTarget);
        return ScrData.Default;
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

        // TODO: undefined can't be compared

        // TODO: this is a blunt instrument and I don't think it's correct
        return new ScrData(ScrDataTypes.Bool, left.Value != right.Value);
    }

    private ScrData AnalyseDotOp(BinaryExprNode node, SymbolTable symbolTable, bool createSenseTokenForField = true)
    {
        if (node.Right!.OperatorType != ExprOperatorType.IdentifierOperand || node.Right is not IdentifierExprNode identifierNode)
        {
            AddDiagnostic(node.Right!.Range, GSCErrorCodes.IdentifierExpected);
            return ScrData.Default;
        }

        ScrData left = AnalyseExpr(node.Left!, symbolTable, Sense);

        // Type unknown - nothing to do.
        if (left.TypeUnknown())
        {
            Sense.AddSenseToken(identifierNode.Token, new ScrFieldSymbol(identifierNode, ScrData.Default));
            return ScrData.Default;
        }

        // TODO: add special case for undefined where it says ... is not defined.
        // Make sure it is a struct, object or entity.
        if (left.Type == ScrDataTypes.Array && identifierNode.Identifier == "size")
        {
            // TODO: evaluate value.
            ScrData sizeValue = new(ScrDataTypes.Int, ReadOnly: true);
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
            Sense.AddSenseToken(identifierNode.Token, new ScrFieldSymbol(identifierNode, ScrData.Default));
            return ScrData.Default;
        }

        ScrData value = left.GetField(identifierNode.Identifier);

        if (createSenseTokenForField)
        {
            Sense.AddSenseToken(identifierNode.Token, new ScrFieldSymbol(identifierNode, value));
        }

        // OK, we got a LHS to look at. Access the field.
        return value;
    }

    private ScrData AnalyseDataExpr(DataExprNode expr)
    {
        return new ScrData(expr.Type, expr.Value);
    }

    private ScrData AnalyseIdentifierExpr(IdentifierExprNode expr, SymbolTable symbolTable, bool createSenseTokenForRhs = true)
    {
        // Analyze and return the corresponding ScrData for the field
        ScrData? value = symbolTable.TryGetVariableSymbol(expr.Identifier, out SymbolFlags flags);
        if (value is not ScrData data)
        {
            return ScrData.Undefined();
        }

        if (data.Type != ScrDataTypes.Undefined)
        {
            if (flags.HasFlag(SymbolFlags.Global) && data.Type == ScrDataTypes.Function)
            {
                if (createSenseTokenForRhs && !flags.HasFlag(SymbolFlags.Reserved) && !data.ValueUnknown())
                {
                    Sense.AddSenseToken(expr.Token, new ScrFunctionReferenceSymbol(expr.Token, data.Get<ScrFunction>()));
                }
                return data;
            }

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

        // TODO: evaluate values later.
        return new ScrData(ScrDataTypes.Vec3);
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

        // TODO: if map, check for string key. If array, check for int index.

        if (collection.Type == ScrDataTypes.Array)
        {
            if (indexer.Type != ScrDataTypes.Int)
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

        ScrData symbol = symbolTable.TryGetNamespacedFunctionSymbol(namespaceNode.Identifier, memberNode.Identifier, out SymbolFlags flags);

        if (flags.HasFlag(SymbolFlags.Global))
        {
            Sense.AddSenseToken(memberNode.Token, new ScrFunctionReferenceSymbol(memberNode.Token, symbol.Get<ScrFunction>()));
        }

        return symbol;
    }

    private ScrData AnalyseFunctionCall(FunCallNode call, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? target = null)
    {
        ScrData targetValue = target ?? ScrData.Default;

        // Get the function we're targeting.
        if (call.Function is null)
        {
            return ScrData.Default;
        }

        ScrData functionTarget = AnalyseExpr(call.Function, symbolTable, sense);

        if (functionTarget.TypeUnknown())
        {
            return ScrData.Default;
        }

        if (functionTarget.Type == ScrDataTypes.Undefined && call.Function is IdentifierExprNode identifierNode)
        {
            string functionName = identifierNode.Identifier;
            AddDiagnostic(call.Function!.Range, GSCErrorCodes.FunctionDoesNotExist, functionName);
            return ScrData.Default;
        }

        if (functionTarget.Type != ScrDataTypes.Function)
        {
            AddDiagnostic(call.Function!.Range, GSCErrorCodes.ExpectedFunction, functionTarget.TypeToString());
            return ScrData.Default;
        }

        ScrFunction function = functionTarget.Get<ScrFunction>();

        // Analyse its arguments.
        foreach (ExprNode? argument in call.Arguments.Arguments)
        {
            if (argument is null)
            {
                continue;
            }

            ScrData argumentValue = AnalyseExpr(argument, symbolTable, sense);

            if (argumentValue.TypeUnknown())
            {
                continue;
            }

            // TODO: Check whether types match up, if we have them.
        }

        return ScrData.Default;
    }

    private ScrData AnalyseThreadedFunctionCall(PrefixExprNode call, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? target = null)
    {
        ScrData targetValue = target ?? ScrData.Default;

        // Analyse the function we're calling.
        ScrData functionTarget = AnalyseExpr(call.Operand, symbolTable, sense);

        // Threaded calls won't return anything.
        return ScrData.Undefined();
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