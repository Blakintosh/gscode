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

import { TokenType } from "../../../../../lexer/tokens/Token";
import { SpecialTokenTypes } from "../../../../../lexer/tokens/types/SpecialToken";
import { GSCProcessNames } from "../../../../../util/GSCUtil";
import { ScriptReader } from "../../../../logic/ScriptReader";
import { TokenRule } from "../../../../logic/TokenRule";
import { LogicalExpression } from "../../../expression/logical/LogicalExpression";
import { AssignmentExpression } from "../../../expression/types/AssignmentExpression";
import { IASTNode } from "../../IASTNode";

/**
 * Variable Assignment Syntax
 * First token can be a Name or a Function call, possibly indexed with [String/Integer]
 * after any sequence of Name, possibly indexed with [String/Integer]
 * Each split by a .
 */

export class VariableAssignment implements IASTNode {
	assignmentExpression: AssignmentExpression = new AssignmentExpression();

	getChildren(): IASTNode[] {
		throw new Error("Method not implemented.");
	}

	/**
	 * Attempts to parse the next tokens as a variable assignment - if it fails, then there is no match
	 * @param reader Reference to the reader
	 * @returns true if matches, false otherwise
	 */
	matches(reader: ScriptReader): boolean {
		const baseIndex = reader.index;

		try {
			this.assignmentExpression.parse(reader);
		} catch(e) {
			reader.index = baseIndex;
			return false;
		}
		return true;
	}

	parse(reader: ScriptReader, allowedChildrenFunc: (() => IASTNode[]) | undefined): void {
		// Expression already parsed, Check for semicolon
		const semiColon = new TokenRule(TokenType.SpecialToken, SpecialTokenTypes.EndStatement);

		if(!semiColon.matches(reader.readToken())) {
			reader.diagnostic.pushDiagnostic(reader.getLastTokenLocation(), "Expected ';'", GSCProcessNames.Parser);
		}
		reader.index++;

		// Done
	}
}