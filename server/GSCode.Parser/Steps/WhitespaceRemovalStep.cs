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

using GSCode.Parser.Steps.Interfaces;

namespace GSCode.Parser.Steps;

internal class WhitespaceRemovalStep : IParserStep
{
    public ScriptTokenLinkedList Tokens { get; }

    public WhitespaceRemovalStep(ScriptTokenLinkedList tokens)
    {
        Tokens = tokens;
    }

    public async Task RunAsync()
    {
        // TODO: Might be able to straight up remove this step now as whitespace is skipped by new token LL
        await Task.Run(() =>
        {
            Token current = Tokens.First!.NextAny();
            while (!current.IsEof())
            {
                Token token = current;
                if (token.Is(TokenType.Whitespace) || token.Is(TokenType.NewLine))
                {
                    Tokens.Remove(current);
                }
                current = current.NextAny();
            }
        });
    }
}
