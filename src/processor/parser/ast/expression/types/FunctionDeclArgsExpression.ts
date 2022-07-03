/**
	GSCode Language Extension for Visual Studio Code
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

import { Token, TokenType } from "../../../../lexer/tokens/Token";
import { ScriptReader } from "../../../logic/ScriptReader";
import { StatementContents } from "../StatementContents";
import { PunctuationTypes } from "../../../../lexer/tokens/types/Punctuation";
import { TokenRule } from "../../../logic/TokenRule";
import { FunctionDeclArgExpression } from "./FunctionDeclArgExpression";
import { SpecialTokenTypes } from "../../../../lexer/tokens/types/SpecialToken";
import { GSCProcessNames } from "../../../../util/GSCUtil";
import { ScriptError } from "../../../diagnostics/ScriptError";

export class FunctionDeclArgsExpression extends StatementContents {
    arguments: FunctionDeclArgExpression[] = [];

    /**
     * Parses the given statement contents, which may include recursive calls.
     * Each argument of a function declaration will individually be recursed to parse.
     */
    parse(reader: ScriptReader): void {
        let token = reader.readToken();
		let startOfArgs = new TokenRule(TokenType.Punctuation, PunctuationTypes.OpenParen);
		let endOfArgs = new TokenRule(TokenType.Punctuation, PunctuationTypes.CloseParen);

		if(startOfArgs.matches(token)) {
			reader.index++;
			while(!endOfArgs.matches(reader.readToken())) {
				// Advance to current argument
				let argument = new FunctionDeclArgExpression();
				argument.parse(reader);

				let comma = new TokenRule(TokenType.SpecialToken, SpecialTokenTypes.Comma);
				let nextToken = reader.readToken();
				if(!comma.matches(nextToken) && !endOfArgs.matches(nextToken)) {
					throw new ScriptError(nextToken.getLocation(), "Expected comma or closing parenthesis.", GSCProcessNames.Parser);
				} else if(comma.matches(nextToken)) {
					reader.index++;
				}
			}
		}

        reader.index++; // Done
    }
}