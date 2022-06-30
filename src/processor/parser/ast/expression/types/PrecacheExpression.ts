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
import { PunctuationTypes } from "../../../../lexer/tokens/types/Punctuation";
import { SpecialTokenTypes } from "../../../../lexer/tokens/types/SpecialToken";
import { GSCProcessNames } from "../../../../util/GSCUtil";
import { ScriptReader } from "../../../logic/ScriptReader";
import { TokenRule } from "../../../logic/TokenRule";
import { StatementContents } from "../StatementContents";

export class PrecacheExpression extends StatementContents {
	/**
	 * Parses the given statement contents, which may include recursive calls.
	 * For precaches there are no nested calls.
	 */
	parse(reader: ScriptReader): void {
		// Read the open parenthesis
		const openParenthesis = reader.readToken();
		const openParenMatch = new TokenRule(TokenType.Punctuation, PunctuationTypes.OpenParen);
		reader.index++;

		if(!openParenMatch.matches(openParenthesis)) {
			reader.diagnostic.pushDiagnostic(openParenthesis.getLocation(), "Expected '('", GSCProcessNames.Parser);
			return;
		}

		// Read the Type
		const type = <Token> reader.readToken();
		reader.index++;

		if(type.getType() !== TokenType.ScriptString) {
			reader.diagnostic.pushDiagnostic(type.getLocation(), "Expected precache type", GSCProcessNames.Parser);
			return;
		}

		// Read the comma
		const comma = reader.readToken();
		reader.index++;

		const commaMatch = new TokenRule(TokenType.SpecialToken, SpecialTokenTypes.Comma);

		if(!commaMatch.matches(comma)) {
			reader.diagnostic.pushDiagnostic(comma.getLocation(), "Expected ','", GSCProcessNames.Parser);
			return;
		}

		// Read the Asset
		const asset = <Token> reader.readToken();
		reader.index++;

		if(asset.getType() !== TokenType.ScriptString) {
			reader.diagnostic.pushDiagnostic(asset.getLocation(), "Expected precache path", GSCProcessNames.Parser);
			return;
		}

		// Read the close parenthesis
		const closeParenthesis = reader.readToken();
		const closeParenMatch = new TokenRule(TokenType.Punctuation, PunctuationTypes.CloseParen);
		reader.index++;

		if(!closeParenMatch.matches(closeParenthesis)) {
			reader.diagnostic.pushDiagnostic(closeParenthesis.getLocation(), "Expected ')'", GSCProcessNames.Parser);
			return;
		}

		// Finally, validate the precache
		//const success = this.value?.populate(reader.format, type.contents, asset.contents);

		/*if(!success) {
			// TODO: Move to simulator
			//reader.diagnostic.pushDiagnostic(type.getLocation(), "Unknown precache type", GSCProcessNames.Parser);
		} else if(this.value) {
			//reader.precaches.push(this.value);
		}*/
	}
}