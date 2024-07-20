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

using GSCode.Data;
using GSCode.Data.Models;
using GSCode.Data.Models.Interfaces;
using GSCode.Lexer.Types;

namespace GSCode.Parser.Preprocessor.Directives
{
    internal sealed class ReferenceMacroHandler : IPreprocessorHandler
    {
        public PreprocessorMutation CreateMutation(Token currentToken, PreprocessorHelper data)
        {
            Token nameToken = currentToken;

            // Add its expansion
            ScriptDefine define = data.Defines[nameToken.Contents];

            List<Token> parameters = define.Parameters;
            Dictionary<string, List<Token>> parametersMap = CreateArgumentDictionary(currentToken, parameters, nameToken, 
                data, out Token finalToken);

            // Add the expansion tokens with the fake position of on the name token
            TokenLinkedList expandedTokens = CreateExpansionList(nameToken, define, parametersMap);

            if (!nameToken.BelowSurfaceLevel)
            {
                ScriptMacro macro = new ScriptMacro(nameToken, define, expandedTokens.ToList());
                data.MacroUses.Add(macro);
                data.Sense.AddSenseToken(macro);
            }

            // Remove the reference, and replace it with the expansion tokens
            return new(finalToken, expandedTokens.First, expandedTokens.Last);
        }

        private static TokenLinkedList CreateExpansionList(Token nameToken, ScriptDefine define, Dictionary<string, List<Token>> parametersMap)
        {
            TokenLinkedList output = new();
            foreach (Token token in define.ExpansionTokens)
            {
                // If this expansion token matches one of the parameters, substitute it for the input parameter contents.
                if (token.Is(TokenType.Name) && parametersMap.ContainsKey(token.Contents))
                {
                    foreach(Token mapToken in parametersMap[token.Contents])
                    {
                        // Shorthand to make a copy of the token - as token stores next/last
                        output.Add(mapToken with { });
                    }
                    continue;
                }

                output.Add(token with { TextRange = nameToken.TextRange, BelowSurfaceLevel = true });
            }
            return output;
        }

        private Dictionary<string, List<Token>> CreateArgumentDictionary(Token baseToken, List<Token> parameters, 
            Token nameToken, PreprocessorHelper data, out Token finalToken)
        {
            Dictionary<string, List<Token>> outputDictionary = new();

            Token followingToken = baseToken.NextNotWhitespace();
            if (!MacroUsesArgs(parameters) || !AnyArgumentsProvided(followingToken, data))
            {
                finalToken = baseToken;
                return outputDictionary;
            }

            finalToken = ParseThroughMacroArguments(followingToken, parameters, outputDictionary);

            AssertCorrectParameterCount(parameters, followingToken, data, outputDictionary, finalToken);
            return outputDictionary;
        }

        private static Token ParseThroughMacroArguments(Token baseToken, List<Token> parameters, Dictionary<string, List<Token>> outputDictionary)
        {
            Token argumentToken = baseToken.NextNotWhitespace();

            foreach (Token argument in parameters)
            {
                List<Token> tokensForArgument = new();

                int nestedParenCount = 0;
                while (!argumentToken.IsEof() && !ArgumentEnded(argumentToken, nestedParenCount))
                {
                    nestedParenCount = UpdateNestedParenthesisCount(argumentToken, nestedParenCount);

                    // Add all the tokens in this parameter
                    tokensForArgument.Add(argumentToken);

                    argumentToken = argumentToken.NextNotWhitespace();
                }

                outputDictionary.Add(argument.Contents, tokensForArgument);

                argumentToken = StepOverIfComma(argumentToken);
            }

            return argumentToken;
        }

        private static int UpdateNestedParenthesisCount(Token argumentToken, int nestedParenCount)
        {
            if (argumentToken.Is(TokenType.Punctuation, PunctuationTypes.OpenParen))
            {
                nestedParenCount++;
            }
            else if(argumentToken.Is(TokenType.Punctuation, PunctuationTypes.CloseParen) &&
                nestedParenCount > 0)
            {
                nestedParenCount--;
            }

            return nestedParenCount;
        }

        private static void AssertCorrectParameterCount(List<Token> parameters, Token nameToken, PreprocessorHelper data, 
            Dictionary<string, List<Token>> outputDictionary, Token argumentToken)
        {
            if (outputDictionary.Count == parameters.Count && 
                !argumentToken.Is(TokenType.Punctuation, PunctuationTypes.CloseParen))
            {
                data.AddSenseDiagnostic(argumentToken.TextRange, GSCErrorCodes.TooManyMacroArguments, nameToken.Contents, parameters.Count);
            }
            else if (outputDictionary.Count < parameters.Count && 
                argumentToken.Is(TokenType.Punctuation, PunctuationTypes.CloseParen))
            {
                data.AddSenseDiagnostic(argumentToken.TextRange, GSCErrorCodes.TooFewMacroArguments, nameToken.Contents, parameters.Count);
            }
        }

        private static Token StepOverIfComma(Token currentToken)
        {
            if (currentToken.Is(TokenType.SpecialToken, SpecialTokenTypes.Comma))
            {
                return currentToken.NextNotWhitespace();
            }

            return currentToken;
        }

        private static bool AnyArgumentsProvided(Token baseToken, PreprocessorHelper data)
        {
            if (!baseToken.Is(TokenType.Punctuation, PunctuationTypes.OpenParen))
            {
                data.AddSenseDiagnostic(baseToken.TextRange, GSCErrorCodes.MissingToken, "(");
                return false;
            }
            return true;
        }

        private static bool ArgumentEnded(Token addToken, int nestedParenCount)
        {
            return nestedParenCount == 0 && (addToken.Is(TokenType.SpecialToken, SpecialTokenTypes.Comma) || 
                addToken.Is(TokenType.Punctuation, PunctuationTypes.CloseParen));
        }

        private static bool MacroUsesArgs(List<Token> parameters)
        {
            return parameters.Count > 0;
        }

        public bool Matches(Token currentToken, PreprocessorHelper data)
        {
            return currentToken.Is(TokenType.Name)
                && data.Defines.ContainsKey(currentToken.Contents);
        }
    }
}
