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

using GSCode.Data.Models;
using GSCode.Data.Models.Interfaces;
using GSCode.Data.Structures;
using GSCode.Lexer.Types;
using GSCode.Lexer.Types.Interfaces;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Serilog;
using System.Diagnostics;

namespace GSCode.Lexer
{
    public record class LexerSourceOverride(bool IsBelowSurface, Range EnforcedRange);

    public sealed class ScriptLexer
    {
        public Uri RootFileUri { get; private set; }
        public ScriptTokenLinkedList Tokens { get; private set; } = new();
        public int EndLine { get; private set; } = 0;

        public ScriptLexer(Uri documentUri)
        {
            RootFileUri = documentUri;
        }

        public async Task TokenizeAsync(string documentText)
        {
            Log.Information("Tokenizing file at '{file}'", RootFileUri);

            Stopwatch sw = Stopwatch.StartNew();
            await Task.Run(() =>
            {
                Tokens = TokenizeScriptContent(documentText, null, out int endLine);
                EndLine = endLine;
                Tokens.AddBorderNodes();
            });

            sw.Stop();
            Log.Information("Tokenized in {0}ms.", sw.Elapsed.TotalMilliseconds);
        }

        private static readonly List<ITokenFactory> tokenFactories = new()
        {
            new MultilineCommentTokenFactory(),
            new SingleLineTokenFactory()
        };

        public static ScriptTokenLinkedList TokenizeScriptContent(string documentText, Range? overrideRange, out int endLine)
        {
            ScriptTokenLinkedList tokens = new();

            try
            {
                ReadOnlySpan<char> documentSpan = documentText.AsSpan();

                int line = 0;
                int position = 0;
                int lineBasePosition = 0;

                while(position < documentSpan.Length)
                {
                    int linePosition = position - lineBasePosition;
                    if (AtLineBreak(documentSpan, position))
                    {
                        lineBasePosition = CreateNewLine(overrideRange, tokens, 1, ref line, ref position, linePosition);
                        continue;
                    }
                    else if (AtWindowsLineBreak(documentSpan, position))
                    {
                        lineBasePosition = CreateNewLine(overrideRange, tokens, 2, ref line, ref position, linePosition);
                        continue;
                    }

                    bool anyMatch = FindFactoryTokenMatch(overrideRange, tokens, documentSpan, ref line, ref position, ref lineBasePosition);

                    if (!anyMatch)
                    {
                        tokens.Add(
                            new Token(overrideRange ?? new Range
                                {
                                    Start = new Position(line, linePosition),
                                    End = new Position(line, linePosition + 1)
                                },
                                TokenType.Unknown, null,
                                documentSpan[position].ToString())
                            {
                                BelowSurfaceLevel = overrideRange is not null
                            });
                        position++;
                    }
                }

                endLine = line;
            }
            catch(Exception ex)
            {
                Log.Error(ex, "Failed to tokenize: '{documentText}'", documentText);
                endLine = 0;
            }

            return tokens;
        }

        private static bool FindFactoryTokenMatch(Range? overridenRange, ScriptTokenLinkedList tokens, ReadOnlySpan<char> documentSpan, ref int line, ref int position, ref int lineBasePosition)
        {
            bool anyMatch = false;
            foreach (ITokenFactory factory in tokenFactories)
            {
                if (factory.HasMatch(documentSpan, ref line, ref lineBasePosition, ref position, out Token token))
                {
                    ApplySurfaceOverride(overridenRange, token);
                    tokens.Add(token);
                    anyMatch = true;
                    break;
                }
            }

            return anyMatch;
        }

        private static bool AtLineBreak(ReadOnlySpan<char> documentSpan, int position)
        {
            return documentSpan[position] == '\n';
        }

        private static bool AtWindowsLineBreak(ReadOnlySpan<char> documentSpan, int position)
        {
            return position + 1 < documentSpan.Length &&
                                    documentSpan[position] == '\r' && documentSpan[position + 1] == '\n';
        }

        private static int CreateNewLine(Range? overrideRange, ScriptTokenLinkedList tokens, int newLineTokenLength, ref int line, ref int position, int linePosition)
        {
            tokens.Add(new Token(
                overrideRange ?? new Range
                {
                    Start = new Position(line, linePosition),
                    End = new Position(line, linePosition + newLineTokenLength)
                },
                TokenType.NewLine, null,
                "\n")
                {
                    BelowSurfaceLevel = overrideRange is not null
                });

            position += newLineTokenLength;
            line++;
            return position;
        }

        private static void ApplySurfaceOverride(Range? overrideRange, Token token)
        {
            if (overrideRange is not null)
            {
                token.BelowSurfaceLevel = true;
                token.TextRange = overrideRange;
            }
        }

        /// <summary>
        /// Gets the upper bound of the line in the file, for slicing the document span into a line.
        /// </summary>
        /// <param name="documentSpan">The full script text as a span</param>
        /// <param name="lowerBound">The beginning index of this line</param>
        /// <returns>A position if newline found or the end of the file reached.</returns>
        private static int GetLineUpperBound(ReadOnlySpan<char> documentSpan, int lowerBound = 0)
        {
            for(int i = lowerBound; i < documentSpan.Length; i++)
            {
                if(documentSpan[i] == '\n')
                {
                    return i;
                }
            }
            return documentSpan.Length;
        }
    }
}