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

//namespace GSCode.Parser.Preprocessor.Directives;

//internal record struct PreprocessorMutation(Token EndOld,
//    Token? StartNew = null, Token? EndNew = null);

//internal interface IPreprocessorHandler
//{
//    /// <summary>
//    /// Generates mutation parameters for the token list to replace nodes from the current node to an end node (inclusive)
//    /// with a new sequence governed by a start and end node.
//    /// </summary>
//    /// <param name="baseToken">The base token</param>
//    /// <param name="data">Reference to the data class</param>
//    /// <returns>A PreprocessorMutation instance specifying old end node and start and end new nodes</returns>
//    public PreprocessorMutation CreateMutation(Token baseToken, PreprocessorHelper data);

//    /// <summary>
//    /// Determines whether the current location's nodes match a preprocessor sequence
//    /// </summary>
//    /// <param name="currentToken">The current token</param>
//    /// <param name="data">Reference to the data class for contextual based matching</param>
//    /// <returns>true if a match, false otherwise</returns>
//    public bool Matches(Token currentToken, PreprocessorHelper data);
//}
