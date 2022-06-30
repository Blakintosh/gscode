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
import { PunctuationTypes } from "../../../../lexer/tokens/types/Punctuation";
import { GSCProcessNames } from "../../../../util/GSCUtil";
import { ScriptReader } from "../../../logic/ScriptReader";
import { TokenRule } from "../../../logic/TokenRule";
import { StatementContents } from "../StatementContents";
import { LogicalExpression } from "./LogicalExpression";

export class ParenBooleanExpression extends StatementContents {
	expression: LogicalExpression = new LogicalExpression();
	
	/**
     * Parses the given statement contents, which may include recursive calls.
     * Boolean expressions recurse through the logical expression to obtain the LHS (and the operator and RHS is always == 1).
     */
	 parse(reader: ScriptReader): void {
		// Parse the LHS
		this.expression.parse(reader);

		// Parse the close paren.
		const closeParen = new TokenRule(TokenType.Punctuation, PunctuationTypes.CloseParen);

		if(!closeParen.matches(reader.readToken())) {
			reader.diagnostic.pushDiagnostic(reader.readToken(-1).getLocation(), "Expected ')'", GSCProcessNames.Parser);
		} else {
			reader.index++;
		}

		// done
    }
}