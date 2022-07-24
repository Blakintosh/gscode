/* eslint-disable @typescript-eslint/naming-convention */
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

import { IToken } from "../../../../lexer/tokens/IToken";
import { Token, TokenType } from "../../../../lexer/tokens/Token";
import { KeywordTypes } from "../../../../lexer/tokens/types/Keyword";
import { NumberTypes } from "../../../../lexer/tokens/types/Number";
import { GSCProcessNames } from "../../../../util/GSCUtil";
import { ScriptError } from "../../../diagnostics/ScriptError";
import { ScriptReader } from "../../../logic/ScriptReader";
import { StatementContents } from "../StatementContents";

export enum LiteralDataTypes {
	String,
	Integer,
	Float,
	Boolean,
	Undefined,
	Unknown
}

export class LiteralDataExpression extends StatementContents {
	value?: Token;
	type: LiteralDataTypes = LiteralDataTypes.Unknown;

	/**
	 * Parses the given statement contents, which may include recursive calls.
	 * For a name there are no nested calls.
	 */
	parse(reader: ScriptReader): void {
		let token = reader.readToken();
		reader.index++;

		// Check if the token is a number
		if(token.getType() === TokenType.Number) {
			this.type = (token.getSpecificType() === NumberTypes.Float) ? LiteralDataTypes.Float : LiteralDataTypes.Integer;
		} else if(token.getType() === TokenType.ScriptString) {
			this.type = LiteralDataTypes.String;
		} else if(token.getType() === TokenType.Keyword) {
			if(token.getSpecificType() === KeywordTypes.True || token.getSpecificType() === KeywordTypes.False) {
				this.type = LiteralDataTypes.Boolean;
			} else if(token.getSpecificType() === KeywordTypes.Undefined) {
				this.type = LiteralDataTypes.Undefined;
			} else {
				// Incorrect usage, abort parsing
				throw new ScriptError(token.getLocation(), "Unexpected keyword.", GSCProcessNames.Parser);
			}
		} else {
			// Incorrect usage, abort parsing
			throw new ScriptError(token.getLocation(), "Unexpected token.", GSCProcessNames.Parser);
		}

		this.value = <Token> token;

		// Done
	}
}