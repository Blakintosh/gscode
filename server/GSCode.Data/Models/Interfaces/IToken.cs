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

namespace GSCode.Data.Models.Interfaces
{
    /// <summary>
    /// GSC primary token types
    /// </summary>
    public enum TokenType
    {
        Unknown,
        Keyword,
        Name,
        Operator,
        Punctuation,
        SpecialToken,
        Whitespace,
        Number,
        ScriptString,
        Comment,
        NewLine,
        Eof,
        Sof
    }

    public interface IToken
    {
        /// <summary>
        /// The location this token is from
        /// </summary>
        public Range TextRange { get; }

        /// <summary>
        /// The primary type of this token
        /// </summary>
        public TokenType Type { get; }

        /// <summary>
        /// The sub-type of this token, which may not apply
        /// </summary>
        public Enum? SubType { get; }

        /// <summary>
        /// The content of this token
        /// </summary>
        public string Contents { get; }

        /// <summary>
        /// Gets whether the token is of the type and subtype provided.
        /// </summary>
        /// <param name="type">Type to match</param>
        /// <param name="subType">Sub type to match</param>
        /// <returns>true if a match</returns>
        public bool Is(TokenType type, Enum subType);

        /// <summary>
        /// Gets whether the token if of the type provided.
        /// </summary>
        /// <param name="type">Type to match</param>
        /// <returns>true if a match</returns>
        public bool Is(TokenType type);
    }
}
