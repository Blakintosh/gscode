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
	import { SpecialTokenTypes } from "../../../../lexer/tokens/types/SpecialToken";
	import { GSCProcessNames } from "../../../../util/GSCUtil";
import { ScriptError } from "../../../diagnostics/ScriptError";
	
	export class ArgumentsExpression extends StatementContents {
		arguments: StatementContents[] = [];
		started: boolean = false;
		ended: boolean = false;
	
		/**
		 * Parses the given statement contents, which may include recursive calls.
		 * Each argument of a function declaration will individually be recursed to parse.
		 */
		parse(reader: ScriptReader): void {
			let token = reader.readToken();
			let startOfArgs = new TokenRule(TokenType.Punctuation, PunctuationTypes.OpenParen);
			let endOfArgs = new TokenRule(TokenType.Punctuation, PunctuationTypes.CloseParen);
			
			reader.index++;
			if(startOfArgs.matches(token)) {
				this.started = true;
				if(endOfArgs.matches(reader.readToken())) {
					reader.index++;
					this.ended = true;
				}
			} else {
				throw new ScriptError(token.getLocation(), "Expected '('", GSCProcessNames.Parser);
			}
			// Done
		}

		advance(reader: ScriptReader): void {
			const token = reader.readToken();
			const startOfArgs = new TokenRule(TokenType.Punctuation, PunctuationTypes.OpenParen);
			const endOfArgs = new TokenRule(TokenType.Punctuation, PunctuationTypes.CloseParen);
			const comma = new TokenRule(TokenType.SpecialToken, SpecialTokenTypes.Comma);

			reader.index++;
			if(endOfArgs.matches(token)) {
				this.ended = true;
			} else if(!comma.matches(token)) {
				throw new ScriptError(token.getLocation(), "Expected ',' or ')'", GSCProcessNames.Parser);
			}
		}
	}