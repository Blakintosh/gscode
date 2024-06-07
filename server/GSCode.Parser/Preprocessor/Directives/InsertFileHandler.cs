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
using GSCode.Lexer;
using GSCode.Lexer.Types;
using GSCode.Parser.Util;

namespace GSCode.Parser.Preprocessor.Directives
{
    internal sealed class InsertFileHandler : IPreprocessorHandler
    {
        public PreprocessorMutation CreateMutation(Token currentToken, PreprocessorHelper data)
        {
            Token pathToken = currentToken.NextNotLinespace();

            // Parse import sequence
            string importPath = ParserUtil.ConvertImportSequenceToString(pathToken, true, out Token lastPathToken)!;
            string? path = ParserUtil.GetScriptFilePath(data.ScriptFile, importPath, null);

            // Remove the semicolon if it was provided
            Token lastToken = CheckSemiColonProvided(lastPathToken, data);

            Range pathRange = Token.RangeBetweenTokens(pathToken, lastPathToken);

            if (string.IsNullOrEmpty(path))
            {
                data.AddSenseDiagnostic(pathRange, GSCErrorCodes.MissingScript, importPath);
                return new(lastToken);
            }

            // Tokenize the destination file & add it
            return InsertTokenizedScriptFromPath(path, pathRange, lastToken);
        }

        private static PreprocessorMutation InsertTokenizedScriptFromPath(string path, Range pathRange, Token oldEndToken)
        {
            string script = File.ReadAllText(path);
            ScriptTokenLinkedList tokens = ScriptLexer.TokenizeScriptContent(script, pathRange, out _);

            return new(oldEndToken, tokens.First, tokens.Last);
        }

        private static Token CheckSemiColonProvided(Token lastPathNode, PreprocessorHelper data)
        {
            Token semiColonNode = lastPathNode.NextNotWhitespace();
            if (!semiColonNode.Is(TokenType.SpecialToken, SpecialTokenTypes.SemiColon))
            {
                data.AddSenseDiagnostic(semiColonNode.CharacterRangeAfterToken(), GSCErrorCodes.MissingToken, ";");
                return lastPathNode;
            }
            return semiColonNode;
        }

        public bool Matches(Token currentToken, PreprocessorHelper data)
        {
            // Checks format matches: #insert [path sequence]
            if(currentToken.Is(TokenType.Keyword, KeywordTypes.Insert) &&
                currentToken.NextNotLinespace().Is(TokenType.Name))
            {
                return true;
            }
            return false;
        }
    }
}
