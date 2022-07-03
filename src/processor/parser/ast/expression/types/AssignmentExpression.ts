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
import { OperatorType } from "../../../../lexer/tokens/types/Operator";
import { GSCProcessNames } from "../../../../util/GSCUtil";
import { ScriptError } from "../../../diagnostics/ScriptError";
import { ScriptReader } from "../../../logic/ScriptReader";
import { TokenRule } from "../../../logic/TokenRule";
import { LogicalExpression } from "../logical/LogicalExpression";
import { StatementContents } from "../StatementContents";
import { VariableExpression } from "./VariableExpression";

/**
 * Assignment expression:
 * [Target] =
 */
export class AssignmentExpression extends StatementContents {
	// If first part of sequence has a namespace, specify it here
	targetExpression: VariableExpression = new VariableExpression();
	valueExpression: LogicalExpression = new LogicalExpression();
	
	/**
	 * Parses the given statement contents, which may include recursive calls.
	 * Variable assignment recurses into a variable target.
	 */
	parse(reader: ScriptReader): void {
		// Parse the target
		this.targetExpression.parse(reader);

		// Parse the equals
		const equals = new TokenRule(TokenType.Operator, OperatorType.Assignment);

		if(!equals.matches(reader.readToken())) {
			throw new ScriptError(reader.readToken().getLocation(), "Expected '='", GSCProcessNames.Parser);
		}
		reader.index++;

		// Parse the value expression
		this.valueExpression.parse(reader);
		
		// Done
	}
}