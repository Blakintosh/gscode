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

import { Token, TokenType } from "../../../../../lexer/tokens/Token";
import { OperatorType } from "../../../../../lexer/tokens/types/Operator";
import { ScriptVariable } from "../../../../data/ScriptVariable";
import { ScriptReader } from "../../../../logic/ScriptReader";
import { TokenRule } from "../../../../logic/TokenRule";
import { StatementContents } from "../../../expression/StatementContents";
import { FunctionDeclArgsExpression } from "../../../expression/types/FunctionDeclArgsExpression";
import { LogicalExpression } from "../../../expression/types/LogicalExpression";
import { StatementNode } from "../../StatementNode";

/**
 * AST Class for Variable Assignments
 * As GSC has no keyword for variable declaration, we need to treat both declaration and assignment in the same class,
 * even though expressions can also handle variable assignment
 */
export class VariableAssignment extends StatementNode {
	valueExpression: LogicalExpression = new LogicalExpression();
	name?: string;
	isDeclaration: boolean = false;

    getContents(): StatementContents {
        throw new Error("Method not implemented.");
    }

    getRule(): TokenRule[] {
        return [
			new TokenRule(TokenType.Name),
            new TokenRule(TokenType.Operator, OperatorType.Assignment)
        ];
    }

	parse(reader: ScriptReader): void {
		// Get variable name and validate it
		this.name = (<Token> reader.readToken()).contents;

		// Get if this variable is a new declaration
		this.isDeclaration = reader.getVar(this.name) === undefined;

		reader.index++;

		// no validation needed on =
		reader.index++;

		// Parse the variable assignment expression
		this.valueExpression.parse(reader);

		// Push to the variable stack
		if(this.isDeclaration) {
			reader.pushVar(new ScriptVariable(this.name));
		}

		console.log("Is variable "+this.name+" new? "+this.isDeclaration);

		// As with every statement
		super.parse(reader);
	}
}