using GSCode.Data;
using GSCode.Parser.AST.Expressions;
using GSCode.Parser.AST.Nodes;
using GSCode.Parser.Data;
using GSCode.Parser.DFA;
using GSCode.Parser.SPA.Data;
using GSCode.Parser.SPA.Logic.Components;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.SPA.Logic.Analysers;

internal static class ExpressionAnalyzer
{
    public static ScrData Analyse(Expression expression, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        if (expression.Failed)
        {
            // Handle invalid expression case
            return ScrData.Default;
        }

        if (expression.Empty)
        {
            // Handle empty expression case.
            return ScrData.Void;
        }

        return AnalyseNode(expression.Root!, symbolTable, sense);
    }

    /// <summary>
    /// Recursive analysis function to translate any node type into a ScrData value.
    /// </summary>
    /// <param name="node">The node to analyse</param>
    /// <param name="symbolTable">Reference to the symbol table</param>
    /// <param name="sense">The IntelliSense visitor</param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public static ScrData AnalyseNode(IExpressionNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext = default)
    {
        return node.NodeType switch
        {
            ExpressionNodeType.Unknown => ScrData.Default,
            ExpressionNodeType.Literal => AnalyseLiteral((TokenNode)node),
            ExpressionNodeType.Field => AnalyseField((TokenNode)node, symbolTable, sense, lhsContext),
            ExpressionNodeType.Operation => AnalyseOperation((OperationNode)node, symbolTable, sense, lhsContext),
            ExpressionNodeType.Enclosure => AnalyseEnclosure((EnclosureNode)node, symbolTable, sense, lhsContext),
            _ => throw new InvalidOperationException($"Unsupported node type: {node.NodeType}"),
        };
    }

    private static ScrData AnalyseLiteral(TokenNode node)
    {
        // Analyze and return the corresponding ScrData for the literal
        Token sourceToken = node.SourceToken;

        return sourceToken.Type switch
        {
            TokenType.Number => ScrData.FromLiteral(sourceToken),
            TokenType.ScriptString => ScrData.FromLiteral(sourceToken),
            TokenType.Keyword => ScrData.FromLiteral(sourceToken),
            _ => ScrData.Default,
        };
    }

    /// <summary>
    /// Analyses and attempts to get the ScrData instance corresponding to this symbol from the symbol table.
    /// The symbol may not exist.
    /// </summary>
    /// <param name="node"></param>
    /// <param name="symbolTable"></param>
    /// <returns></returns>
    private static ScrData AnalyseField(TokenNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext)
    {
        // Looking at a property instead
        if(lhsContext is ScrData left)
        {
            return AnalyseProperty(node, sense, left);
        }

        // Analyze and return the corresponding ScrData for the field
        ScrData? value = symbolTable.TryGetSymbol(node.SourceToken.Contents, out bool isGlobal);
        if(value is not ScrData data)
        {
            return ScrData.Default;
        }

        if(data.Type != ScrDataTypes.Undefined)
        {
            if(isGlobal)
            {
                sense.AddSenseToken(ScrVariableSymbol.LanguageSymbol(node, data));
                return data;
            }
            sense.AddSenseToken(ScrVariableSymbol.Usage(node, data));
        }
        return data;
    }

    private static ScrData AnalyseProperty(TokenNode node, ParserIntelliSense sense, ScrData left)
    {
        // Gets the member that corresponds to the property, or undefined if the member doesn't exist
        ScrData member = left.GetField(node.SourceToken.Contents);

        if(member.IsVoid())
        {
            // TODO: this should print a different error if the issue is due to the lhs not actually being defined
            //if(left.Type == ScrDataTypes.Undefined)
            //{
            //    sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(node.Range, DiagnosticSources.SPA, GSCErrorCodes.NotDefined, node.SourceToken.Contents));
            //}
            sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(node.Range, DiagnosticSources.SPA, GSCErrorCodes.DoesNotContainMember, node.SourceToken.Contents, left.TypeToString()));
            return ScrData.Default;
        }

        if (member.Type != ScrDataTypes.Undefined)
        {
            sense.AddSenseToken(new ScrPropertySymbol(node, member, false));
        }

        return member;
    }

    private static ScrData AnalyseOperation(OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext)
    {
        // Analyze and return the corresponding ScrData for the operation
        return node.EvaluationFunction(node, symbolTable, sense, lhsContext);
    }

    private static ScrData AnalyseEnclosure(EnclosureNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext)
    {
        return node.EnclosureType switch
        {
            EnclosureType.Parenthesis => AnalyseParentheses(node, symbolTable, sense),
            EnclosureType.Bracket => AnalyseBrackets(node, symbolTable, sense, lhsContext),
            _ => ScrData.Default,// TODO: implement
        };
    }

    private static ScrData AnalyseParentheses(EnclosureNode node, SymbolTable symbolTable, ParserIntelliSense sense)
    {
        // TODO: Do a ScrData.Void
        if (node.InteriorNodes.Count == 0)
        {
            return ScrData.Void;
        }

        // Analyze and return the corresponding ScrData for the enclosure
        return AnalyseNode(node.InteriorNodes[0], symbolTable, sense);
    }

    private static ScrData AnalyseBrackets(EnclosureNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext)
    {
        if(node.InteriorNodes.Count == 0)
        {
            if (lhsContext is not null)
            {
                sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(node.Range, DiagnosticSources.SPA, GSCErrorCodes.IndexerExpected));
                return ScrData.Default;
            }

            return new ScrData(ScrDataTypes.Array);
        }

        if (lhsContext is not null)
        {
            return AnalyseNode(node.InteriorNodes[0], symbolTable, sense);
        }

        // ERROR: Square bracket collection initialisation is not supported in GSC. Use array() instead.
        sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(node.Range, DiagnosticSources.SPA, GSCErrorCodes.SquareBracketInitialisationNotSupported));

        return ScrData.Default;
    }
}