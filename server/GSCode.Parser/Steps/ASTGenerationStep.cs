/**
	GSCode.NET Language Server
    Copyright (C) 2022 Blakintosh

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using GSCode.Parser.AST;
using GSCode.Parser.AST.Nodes;
using GSCode.Parser.Data;
using GSCode.Parser.SPA.Logic.Analysers;
using GSCode.Parser.Steps.Interfaces;
using Serilog;

namespace GSCode.Parser.Steps;

internal sealed class ASTGenerationStep : IParserStep, ISenseProvider
{
    public ASTHelper Data { get; }
    public ParserIntelliSense Sense { get; }
    public ScriptTokenLinkedList Tokens { get; }
    public ASTNode RootNode { get; } = new(NodeTypes.File, new(), null!, null!, new FileAnalyser(), null, new RootASTBranch());

    internal ASTGenerationStep(ParserIntelliSense sense, string scriptFile, ScriptTokenLinkedList tokens)
    {
        Data = new(scriptFile, sense);
        Sense = sense;
        Tokens = tokens;
    }

    public void Run()
    {
        ParseFromRoot(Tokens);
    }

    private void ParseFromRoot(ScriptTokenLinkedList tokens)
    {
        ParseBranch(tokens.First!.NextConcrete(), RootNode.Branch!);
    }

    /// <summary>
    /// Parses the given branch. Terminates at the end of the file, or when the end pattern is matched.
    /// </summary>
    /// <param name="currentToken">The current token in the list</param>
    /// <param name="branch">Reference to the branch</param>
    /// <returns>An instance of LinkedTokenNode if should continue parsing, otherwise null.</returns>
    private Token? ParseBranch(Token currentToken, ASTBranch branch)
    {
        if(!branch.Open(ref currentToken, Data))
        {
            return currentToken;
        }

        bool closed = branch.Close(ref currentToken);
        while (!currentToken.IsEof() && !closed)
        {
            ASTNode? nextChild = GetNextNode(ref currentToken, branch);
            if(nextChild is not null)
            {
                branch.AddChild(nextChild);

                // Parse the child branch provided
                if(nextChild.Branch is not null)
                {
                    // eof?
                    currentToken = ParseBranch(currentToken, nextChild.Branch)!;
                    if(currentToken is null)
                    {
                        return null;
                    }    
                }
            }
            closed = branch.Close(ref currentToken);
        }

        if (!closed)
        {
            branch.EofReached(currentToken, Data);
            return null;
        }

        return currentToken;
    }

    private ASTNode? GetNextNode(ref Token currentToken, ASTBranch branch)
    {
        IASTNodeFactory? factory = GetFactory(currentToken, branch);

        bool validChildFailed = false;
        if(factory is not null)
        {
            ASTNode? child = factory.Parse(ref currentToken, Data, branch.ChildrenFactories);

            if(child is not null)
            {
                return child;
            }
            validChildFailed = true;
        }

        if (!validChildFailed)
        {
            if(currentToken.Is(TokenType.Unknown))
            {
                Data.AddDiagnostic(currentToken.TextRange, GSCode.Data.GSCErrorCodes.UnexpectedCharacter, currentToken.Contents);
            }
            else if(currentToken.Is(TokenType.Eof))
            {
                Data.AddDiagnostic(currentToken.TextRange, GSCode.Data.GSCErrorCodes.UnexpectedEof);
                return null;
            }
            else
            {
                Data.AddDiagnostic(currentToken.TextRange, GSCode.Data.GSCErrorCodes.TokenNotValidInContext, currentToken.Contents);
            }
        }

        do
        {
            currentToken = currentToken.NextConcrete();
            factory = GetFactory(currentToken, branch);

            if (factory is not null)
            {
                ASTNode? child = factory.Parse(ref currentToken, Data, branch.ChildrenFactories);

                if (child is not null)
                {
                    return child;
                }
            }
        } while (!branch.Close(ref currentToken) && 
            !currentToken.Is(TokenType.Eof));

        Log.Error("No valid child found for branch {0} at token {1}", branch.GetType().Name, currentToken.Contents);
        return null;
    }

    private IASTNodeFactory? GetFactory(Token currentNode, ASTBranch branch)
    {
        foreach(IASTNodeFactory factory in branch.ChildrenFactories)
        {
            if(factory.Matches(currentNode))
            {
                return factory;
            }
        }
        return null;
    }
}
