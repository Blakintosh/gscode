

using GSCode.Data;
using GSCode.Data.Models;
using GSCode.Data.Models.Interfaces;
using GSCode.Lexer.Types;
using GSCode.Parser.Util;
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
namespace GSCode.Parser.Preprocessor.Directives
{
    internal sealed class DefineMacroHandler : IPreprocessorHandler
    {
        public PreprocessorMutation CreateMutation(Token baseToken, PreprocessorHelper data)
        {
            Token nameToken = baseToken.NextNotLinespace();

            List<Token> parameters = ReadParametersIfSpecified(nameToken, data, out Token expansionBaseLocation);
            List<Token> expandedList = ParseExpansion(expansionBaseLocation, out Token lastExpansionNode, out Token? documentationToken);
            List<Token> rawDefineTokens = GetDefineRawTokens(baseToken, expansionBaseLocation, expandedList);

            // Set as a define
            // TODO: Standard token linked list that the one for script files inherits from
            ScriptDefine result = new ScriptDefine(nameToken, rawDefineTokens,
                expandedList, parameters, ParserUtil.GetCommentContents(documentationToken?.Contents, (CommentTypes?)documentationToken?.SubType));
            data.Defines[nameToken.Contents] = result;

            if(!nameToken.BelowSurfaceLevel)
            {
                data.Sense.AddSenseToken(result);
            }

            // Remove the entire define line from the token list
            return new PreprocessorMutation(lastExpansionNode);
        }

        private static List<Token> GetDefineRawTokens(Token currentToken, Token expansionBaseLocation, List<Token> expandedList)
        {
            List<Token> rawDefineTokens = currentToken.GetTokenListUpToToken(expansionBaseLocation);
            rawDefineTokens.AddRange(expandedList);
            return rawDefineTokens;
        }

        private static List<Token> ParseExpansion(Token baseToken,
            out Token lastExpansionToken, out Token? documentationToken)
        {
            List<Token> expandedList = new();

            Token previousToken = baseToken;
            Token currentToken = baseToken.NextAny();
            while (!currentToken.IsEof())
            {
                if (AtLineContinuation(currentToken))
                {
                    currentToken = currentToken.NextNotNewline();
                    continue;
                }

                if(currentToken.Is(TokenType.NewLine))
                {
                    break;
                }

                if(!currentToken.Is(TokenType.Comment) && !AtAdjacentWhitespaceToken(expandedList, currentToken))
                {
                    expandedList.Add(currentToken);
                }

                if(!currentToken.Is(TokenType.Whitespace))
                {
                    previousToken = currentToken;
                }
                currentToken = currentToken.NextAny();
            }

            documentationToken = GetDocumentationIfProvided(previousToken);
            lastExpansionToken = currentToken.IsEof() ? currentToken.Previous! : currentToken;
            return expandedList;
        }

        private static bool AtLineContinuation(Token currentToken)
        {
            return currentToken.Is(TokenType.SpecialToken, SpecialTokenTypes.Backslash) &&
                currentToken.NextAny().Is(TokenType.NewLine);
        }

        private static bool AtAdjacentWhitespaceToken(List<Token> expandedList, Token token)
        {
            if (expandedList.Count == 0)
            {
                return false;
            }

            Token lastAddedToken = expandedList[^1];
            return lastAddedToken.Is(TokenType.Whitespace) && token.Is(TokenType.Whitespace);
        }

        private static Token? GetDocumentationIfProvided(Token previousToken)
        {
            return previousToken.Is(TokenType.Comment) ?
                previousToken : null;
        }

        public bool Matches(Token currentToken, PreprocessorHelper data)
        {
            return currentToken.Is(TokenType.Keyword, KeywordTypes.Define) &&
                currentToken.NextNotLinespace().Is(TokenType.Name);
        }

        private static List<Token> ReadParametersIfSpecified(Token baseToken, PreprocessorHelper data, out Token newNodeLocation)
        {
            List<Token> parameters = new();
            // Check for (
            Token currentToken = baseToken.NextAny();
            if (!currentToken.Is(TokenType.Punctuation, PunctuationTypes.OpenParen))
            {
                newNodeLocation = baseToken;
                return parameters;
            }

            newNodeLocation = ProcessThroughDefineParameters(data, parameters, currentToken);
            return parameters;
        }

        private static Token ProcessThroughDefineParameters(PreprocessorHelper data, List<Token> parameters, Token currentToken)
        {
            Token newNodeLocation;
            do
            {
                currentToken = currentToken.NextNotLinespace();
                AddDefineParameter(data, parameters, currentToken);

                // Advance to what should be the next comma
                currentToken = currentToken.NextNotLinespace();
            }
            while (currentToken.Is(TokenType.Operator, OperatorTypes.Comma));

            // Check for )
            if (!currentToken.Is(TokenType.Punctuation, PunctuationTypes.CloseParen))
            {
                data.AddSenseDiagnostic(currentToken.TextRange, GSCErrorCodes.MissingToken, ")");
            }

            newNodeLocation = currentToken;
            return newNodeLocation;
        }

        private static void AddDefineParameter(PreprocessorHelper data, List<Token> parameters, Token token)
        {
            if (token.Is(TokenType.Name))
            {
                parameters.Add(token);
            }
            else
            {
                data.AddSenseDiagnostic(token.TextRange, GSCErrorCodes.MissingIdentifier);
            }
        }
    }
}
