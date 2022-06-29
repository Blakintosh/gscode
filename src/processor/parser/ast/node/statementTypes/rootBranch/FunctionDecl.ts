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
import { KeywordTypes } from "../../../../../lexer/tokens/types/Keyword";
import { GSCBranchNodes } from "../../../../../util/GSCUtil";
import { ScriptDependency } from "../../../../data/ScriptDependency";
import { ScriptReader } from "../../../../logic/ScriptReader";
import { TokenRule } from "../../../../logic/TokenRule";
import { StatementContents } from "../../../expression/StatementContents";
import { FilePathExpression } from "../../../expression/types/FilePathExpression";
import { FunctionDeclArgsExpression } from "../../../expression/types/FunctionDeclArgsExpression";
import { StatementNode } from "../../StatementNode";
import { VariableAssignment } from "../function/VariableAssignment";

export class FunctionDecl extends StatementNode {
    //file: FilePathExpression = new FilePathExpression();
	arguments: FunctionDeclArgsExpression = new FunctionDeclArgsExpression();

	constructor() {
		super();
		// A function declaration is a branching statement node
		super.expectsBranch = true;
	}

    getContents(): StatementContents {
        throw new Error("Method not implemented.");
    }

    getRule(): TokenRule[] {
        return [
            new TokenRule(TokenType.Keyword, KeywordTypes.Function),
			new TokenRule(TokenType.Name)
        ];
    }

	parse(reader: ScriptReader): void {
		// Once parsing, specify expected children to avoid callstack error
		super.expectedChildren = GSCBranchNodes.Standard();

		// Store keyword position
		let keywordPosition = reader.readToken().getLocation();

		// No further validation required on the keyword, advance by one token.
		reader.index++;

		// Validate the function name and add it to the function names list.
		reader.index++;

		// Parse the function arguments expression.
		this.arguments.parse(reader); 

		// Parse the file path expression.
		//this.file.parse(reader);

		// Add this as a dependency
		//if(this.file.filePath && this.file.location) {
		//	reader.dependencies.push(new ScriptDependency(this.file.filePath, [keywordPosition[0], this.file.location[1]]));
		//}

		// Use at the end of every subclass of a statement node.
		super.parse(reader);
	}
}