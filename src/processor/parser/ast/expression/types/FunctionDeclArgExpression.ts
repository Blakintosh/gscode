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
import { OperatorType } from "../../../../lexer/tokens/types/Operator";
import { GSCProcessNames } from "../../../../util/GSCUtil";
import { ScriptReader } from "../../../logic/ScriptReader";
import { TokenRule } from "../../../logic/TokenRule";
import { StatementContents } from "../StatementContents";
import { LogicalExpression } from "../logical/LogicalExpression";
import { ScriptError } from "../../../diagnostics/ScriptError";

export class FunctionDeclArgExpression extends StatementContents {
    name?: string;
	// TODO: Change to an expression type explicitly
	default?: LogicalExpression;

    /**
     * Parses the given statement contents, which may include recursive calls.
     * A function argument will recurse through an expression if there is a default value specified.
     */
    parse(reader: ScriptReader): void {
		// Read name then advance the reader
        let nameToken = reader.readToken();
		reader.index++;
		if(nameToken.getType() === TokenType.Name) {
			this.name = (<Token> nameToken).contents;
		} else {
			throw new ScriptError(nameToken.getLocation(), "Expected argument name.", GSCProcessNames.Parser);
		}

		// Check if there is a default value
        let token = reader.readToken();
		let defaultMatcher = new TokenRule(TokenType.Operator, OperatorType.Assignment);

		if(defaultMatcher.matches(token)) {
			// There is a default value, parse this
			reader.index++;

			// Parse expression
			this.default = new LogicalExpression();
			this.default.parse(reader);
		}

		// Done
    }
}