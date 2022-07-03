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

import { TokenType } from "../../../../lexer/tokens/Token";
import { KeywordTypes } from "../../../../lexer/tokens/types/Keyword";
import { PunctuationTypes } from "../../../../lexer/tokens/types/Punctuation";
import { SpecialTokenTypes } from "../../../../lexer/tokens/types/SpecialToken";
import { GSCProcessNames } from "../../../../util/GSCUtil";
import { ScriptError } from "../../../diagnostics/ScriptError";
import { ScriptReader } from "../../../logic/ScriptReader";
import { TokenRule } from "../../../logic/TokenRule";
import { LogicalExpression } from "../logical/LogicalExpression";
import { StatementContents } from "../StatementContents";
import { AssignmentExpression } from "../types/AssignmentExpression";
import { NameExpression } from "../types/NameExpression";
import { VariableExpression } from "../types/VariableExpression";

export class ForeachLoopExpression extends StatementContents {
	nameExpression: NameExpression = new NameExpression();
	sourceExpression: VariableExpression = new VariableExpression();

	/**
	 * Parses the given statement contents, which may include recursive calls.
	 * For a name there are no nested calls.
	 */
	parse(reader: ScriptReader): void {
		let token = reader.readToken();
		reader.index++;

		const openParen = new TokenRule(TokenType.Punctuation, PunctuationTypes.OpenParen);
		const closeParen = new TokenRule(TokenType.Punctuation, PunctuationTypes.CloseParen);

		if(!openParen.matches(token)) {
			throw new ScriptError(token.getLocation(), "Expected '('", GSCProcessNames.Parser);
		}

		// Parse the assignment expression
		try {
			this.nameExpression.parse(reader);
		} catch(e) {
			reader.diagnostic.pushFromError(e);
		}

		// Parse 'in'
		const inKeyword = new TokenRule(TokenType.Keyword, KeywordTypes.In);

		if(!inKeyword.matches(reader.readToken())) {
			reader.diagnostic.pushDiagnostic(reader.getLastTokenLocation(), "Expected 'in'", GSCProcessNames.Parser);
		}
		reader.index++;

		// Parse the source expression
		try {
			this.sourceExpression.parse(reader);
		} catch(e) {
			reader.diagnostic.pushFromError(e);
		}

		// Parse the closing parenthesis
		if(!closeParen.matches(reader.readToken())) {
			reader.diagnostic.pushDiagnostic(reader.getLastTokenLocation(), "Expected ')'", GSCProcessNames.Parser);
		}
		reader.index++;

		// Done
	}
}