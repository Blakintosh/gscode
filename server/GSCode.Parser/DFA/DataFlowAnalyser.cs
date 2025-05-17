using System.Collections.ObjectModel;
using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.CFA;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.SPA;
using GSCode.Parser.SPA.Logic.Components;

namespace GSCode.Parser.DFA;

internal ref struct DataFlowAnalyser(List<Tuple<ScrFunction, ControlFlowGraph>> functionGraphs, ParserIntelliSense sense, Dictionary<string, IExportedSymbol> exportedSymbolTable)
{
    public List<Tuple<ScrFunction, ControlFlowGraph>> FunctionGraphs { get; } = functionGraphs;
    public ParserIntelliSense Sense { get; } = sense;
    public Dictionary<string, IExportedSymbol> ExportedSymbolTable { get; } = exportedSymbolTable;

    public void Run()
    {
        foreach (Tuple<ScrFunction, ControlFlowGraph> functionGraph in FunctionGraphs)
        {
            ForwardAnalyse(functionGraph.Item1, functionGraph.Item2);
        }
    }

    public void ForwardAnalyse(ScrFunction function, ControlFlowGraph functionGraph)
    {
        Dictionary<CfgNode, Dictionary<string, ScrVariable>> inSets = new();
        Dictionary<CfgNode, Dictionary<string, ScrVariable>> outSets = new();

        Stack<CfgNode> worklist = new();
        worklist.Push(functionGraph.Start);

        while (worklist.Count > 0)
        {
            CfgNode node = worklist.Pop();

            // Calculate the in set
            Dictionary<string, ScrVariable> inSet = new();
            foreach (CfgNode incoming in node.Incoming)
            {
                if (outSets.TryGetValue(incoming, out Dictionary<string, ScrVariable>? value))
                {
                    inSet.MergeTables(value, node.Scope);
                }
            }

            // Check if the in set has changed, if not, then we can skip this node.
            if (inSets.TryGetValue(node, out Dictionary<string, ScrVariable>? currentInSet) && currentInSet.VariableTableEquals(inSet))
            {
                continue;
            }

            // Update the in & out sets
            inSets[node] = inSet;

            if (!outSets.ContainsKey(node))
            {
                outSets[node] = new Dictionary<string, ScrVariable>();
            }

            // Calculate the out set
            if (node.Type == CfgNodeType.FunctionEntry)
            {
                outSets[node].MergeTables(inSet, node.Scope);
            }
            else if (node.Type == CfgNodeType.BasicBlock)
            {
                SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope);

                // TODO: Unioning of sets is not ideal, better to merge the ScrDatas of common key across multiple dictionaries. Easier to use with the symbol tables.
                // TODO: Analyse statement-by-statement, using the analysers already created, and get the out set.
                //Analyse(node, symbolTable, inSets, outSets, Sense);
                //outSet.UnionWith(symbolTable.GetOutgoingSymbols());
                AnalyseBasicBlock((BasicBlock)node, symbolTable);

                outSets[node] = symbolTable.VariableSymbols;
            }

            // Add the successors to the worklist 
            foreach (CfgNode successor in node.Outgoing)
            {
                worklist.Push(successor);
            }
        }
    }

    public void AnalyseBasicBlock(BasicBlock block, SymbolTable symbolTable)
    {
        LinkedList<AstNode> logic = block.Statements;

        if(logic.Count == 0)
        {
            return;
        }

        for(LinkedListNode<AstNode>? node = logic.First; node != null; node = node.Next)
        {
            AstNode child = node.Value;

            AstNode? last = node.Previous?.Value;
            AstNode? next = node.Next?.Value;

            AnalyseStatement(child, last, next, symbolTable);
        }
    }

    public void AnalyseStatement(AstNode statement, AstNode? last, AstNode? next, SymbolTable symbolTable)
    {
        switch(statement.NodeType)
        {
            case AstNodeType.ExprStmt:
                AnalyseExprStmt((ExprStmtNode)statement, last, next, symbolTable);
                break;
            default:
                break;
        };
    }

    public void AnalyseExprStmt(ExprStmtNode statement, AstNode? last, AstNode? next, SymbolTable symbolTable)
    {
        ScrData result = AnalyseExpr(statement.Expr, symbolTable, Sense);
    }

    public ScrData AnalyseExpr(ExprNode expr, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        switch(expr.OperatorType)
        {
            case ExprOperatorType.Binary:
                AnalyseBinaryExpr((BinaryExprNode)expr, symbolTable);
                break;
                
        }

        return ScrData.Default;
    }

    public void AnalyseBinaryExpr(BinaryExprNode binary, SymbolTable symbolTable)
    {
        switch(binary.Operation)
        {
            case TokenType.Assign:
                AnalyseAssignOp(binary, symbolTable);
                break;
        }
    }

    public ScrData AnalyseAssignOp(BinaryExprNode node, SymbolTable symbolTable)
    {
        ScrData left = AnalyseExpr(node.Left!, symbolTable, Sense);
        ScrData right = AnalyseExpr(node.Right!, symbolTable, Sense);

        // Assigning to a local variable
        if(node.Left is IdentifierExprNode identifier)
        {
            string symbolName = identifier.Identifier;

            if(left.ReadOnly)
            {
                Sense.AddSpaDiagnostic(identifier.Range, GSCErrorCodes.CannotAssignToConstant, symbolName);
                return ScrData.Default;
            }

            bool isNew = symbolTable.AddOrSetSymbol(symbolName, right);

            if(isNew && right.Type != ScrDataTypes.Undefined)
            {
                Sense.AddSenseToken(identifier.Token, ScrVariableSymbol.Declaration(identifier, right));
            }

            return right;
        }

        // Assigning to a property on a struct
        // if(node.Left is OperationNode operationNode && operationNode.Operation == OperatorOps.MemberAccess && left.Owner is ScrStruct destination)
        // {
        //     TokenNode leafNode = operationNode.FarRightTokenLeaf;
        //     string propertyName = leafNode.SourceToken.Contents;

        //     if(left.ReadOnly)
        //     {
        //         sense.AddSpaDiagnostic(leafNode.Range, GSCErrorCodes.CannotAssignToReadOnlyProperty, propertyName);
        //         return ScrData.Default;
        //     }

        //     destination.Set(propertyName, right);
        //     return right;
        // }

        // sense.AddSpaDiagnostic(node.Left!.Range, GSCErrorCodes.InvalidAssignmentTarget);
        return ScrData.Default;
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
               if(target.TryGetValue(field, out ScrVariable? targetData))
               {
                   if(sourceData != targetData)
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