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
using GSCode.Parser.Data;
using GSCode.Parser.Preprocessor;
using GSCode.Parser.Preprocessor.Directives;
using GSCode.Parser.Steps.Interfaces;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace GSCode.Parser.Steps
{
    internal sealed class PreprocessorStep : IParserStep, ISenseProvider
    {
        public List<IPreprocessorHandler> Handlers { get; } = new()
        {
            new DefineMacroHandler(),
            new InsertFileHandler(),
            new ReferenceMacroHandler()
        };

        public PreprocessorHelper Data { get; }
        public ParserIntelliSense Sense { get; }
        public ScriptTokenLinkedList Tokens { get; }

        internal PreprocessorStep(ParserIntelliSense sense, string scriptFile, ScriptTokenLinkedList tokens)
        {
            Sense = sense;
            Data = new(scriptFile, Sense);
            Tokens = tokens;
        }

        public void Run()
        {
            Token currentToken = Tokens.First!.NextAny();
            while (!currentToken.IsEof())
            {
                bool match = false;
                foreach(IPreprocessorHandler handler in Handlers)
                {
                    if (handler.Matches(currentToken, Data))
                    {
                        PreprocessorMutation mutation = handler.CreateMutation(currentToken, Data);
                        match = true;

                        Tokens.ReplaceRangeInclusive(currentToken, mutation.EndOld, mutation.StartNew, mutation.EndNew);
                        currentToken = mutation.StartNew ?? currentToken.Previous ?? Tokens.First!;

                        if(currentToken.IsSof())
                        {
                            currentToken = currentToken.NextAny();
                        }
                        break;
                    }
                }

                if (match)
                {
                    continue;
                }
                currentToken = currentToken.NextAny();
            }
        }
    }
}
