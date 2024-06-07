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

using GSCode.Data.Models.Interfaces;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace GSCode.Data.Models
{
    public record class Token : IToken
    {
        public Range TextRange { get; set; }
        public TokenType Type { get; init; }
        public Enum? SubType { get; init; }
        public string Contents { get; init; }
        internal Token? Next { get; set; }
        public Token? Previous { get; internal set; }
        public bool BelowSurfaceLevel { get; set; } = false;

        public Token(Range range, TokenType type, Enum? subType, string contents)
        {
            TextRange = range;
            Type = type;
            SubType = subType;
            Contents = contents;
        }

        public bool Is(TokenType type, Enum? subType)
        {
            if (subType == null)
            {
                return Is(type);
            }
            return Type == type && SubType!.Equals(subType);
        }

        public bool Is(TokenType type)
        {
            return Type == type;
        }

        public static Range RangeBetweenTokens(Token token1, Token token2)
        {
            return new Range
            {
                Start = token1.TextRange.Start,
                End = token2.TextRange.End
            };
        }

        public Range CharacterRangeAfterToken()
        {
            return new Range
            {
                Start = TextRange.End,
                End = new Position(TextRange.End.Line, TextRange.End.Character + 1)
            };
        }

        #region Linked List Functionality

        private static readonly TokenType[] noWhitespaceExclusion = new[] { TokenType.NewLine, TokenType.Whitespace };
        /// <summary>
        /// Get the next token node linked to this one whose token is not of type NewLine or Whitespace.
        /// </summary>
        /// <returns>A Token in the list.</returns>
        public Token NextNotWhitespace()
        {
            return NextOf(noWhitespaceExclusion);
        }

        private static readonly TokenType[] noLinespaceExclusion = new[] { TokenType.Whitespace };
        /// <summary>
        /// Get the next token node linked to this one whose token is not of type Whitespace.
        /// </summary>
        /// <returns>A Token in the list.</returns>
        public Token NextNotLinespace()
        {
            return NextOf(noLinespaceExclusion);
        }

        private static readonly TokenType[] noNewLineExclusion = new[] { TokenType.NewLine };
        /// <summary>
        /// Get the next token node linked to this one whose token is not of type NewLine.
        /// </summary>
        /// <returns>A Token in the list.</returns>
        public Token NextNotNewline()
        {
            return NextOf(noNewLineExclusion);
        }

        private static readonly TokenType[] concreteExclusion = new[] { TokenType.NewLine, TokenType.Whitespace, TokenType.Comment };
        /// <summary>
        /// Get the next token node linked to this one whose token is not of type NewLine, Whitespace or Comment.
        /// </summary>
        /// <returns>A Token in the list.</returns>
        public Token NextConcrete()
        {
            return NextOf(concreteExclusion);
        }

        /// <summary>
        /// Get the next token node linked to this one regardless of token type.
        /// </summary>
        /// <returns>A Token in the list.</returns>
        public Token NextAny()
        {
            return NextOf(Array.Empty<TokenType>());
        }

        private Token NextOf(TokenType[] exclusions)
        {
            Token? current = Next;
            while (current is not null)
            {
                Token nextToken = current;
                if (!IsExcluded(nextToken, exclusions))
                {
                    return current;
                }

                current = current.Next;
            }

            throw new NullReferenceException("NextOf() called on a linked list with a null Next node." +
                        "The token linked list should not contain nodes that have no Next neighbour (have you " +
                        "read from a removed node sequence?)");
        }

        private static bool IsExcluded(Token token, TokenType[] exclusions)
        {
            foreach (var exclusion in exclusions)
            {
                if (token.Type == exclusion)
                {
                    return true;
                }
            }
            return false;
        }

        public bool IsEof()
        {
            return Is(TokenType.Eof);
        }
        public bool IsSof()
        {
            return Is(TokenType.Sof);
        }

        public List<Token> GetTokenListUpToToken(Token end)
        {
            List<Token> list = new();

            Token current = this;
            while (current != end && !current.IsEof())
            {
                list.Add(current);
                current = current.NextAny();
            }

            list.Add(current);
            return list;
        }
        #endregion
    }
}
