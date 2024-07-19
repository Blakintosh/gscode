using GSCode.Lexer.Types;
using GSCode.Parser.AST.Nodes;
using GSCode.Parser.Data;
using GSCode.Parser.SPA.Logic.Components;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.SPA.Logic.Analysers;

internal abstract class DataFlowNodeAnalyser
{
    public abstract void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, SymbolTable symbolTable, ParserIntelliSense sense);
}

internal abstract class SignatureNodeAnalyser
{
    public abstract void Analyse(ASTNode currentNode, ASTNode? previousNode, ASTNode? nextNode, DefinitionsTable definitionsTable, ParserIntelliSense sense);

    protected static Token? GetDocComment(ASTNode currentNode)
    {
        if (currentNode == null)
        {
            return null;
        }

        Token startToken = currentNode.StartToken;

        if (startToken.Previous is Token lastToken &&
            lastToken.Is(TokenType.Comment, CommentTypes.Documentation))
        {
            return lastToken;
        }
        return null;
    }
}