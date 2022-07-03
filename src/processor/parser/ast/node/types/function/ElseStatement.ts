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
import { PunctuationTypes } from "../../../../../lexer/tokens/types/Punctuation";
import { GSCBranchNodes } from "../../../../../util/GSCUtil";
import { ScriptDiagnostic } from "../../../../diagnostics/ScriptDiagnostic";
import { ScriptReader } from "../../../../logic/ScriptReader";
import { TokenRule } from "../../../../logic/TokenRule";
import { StatementContents } from "../../../expression/StatementContents";
import { FilePathExpression } from "../../../expression/types/FilePathExpression";
import { FunctionDeclArgsExpression } from "../../../expression/types/FunctionDeclArgsExpression";
import { ParenBooleanExpression } from "../../../expression/types/ParenBooleanExpression";
import { StatementNode } from "../../StatementNode";
import { VariableAssignment } from "./VariableAssignment";

export class ElseStatement extends StatementNode {
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
			new TokenRule(TokenType.Keyword, KeywordTypes.Else)
        ];
    }

	parse(reader: ScriptReader): void {
		// Once at parsing stage, express expected children to avoid a call stack error
		super.expectedChildren = GSCBranchNodes.Standard();
		
		// Store keyword position
		let keywordPosition = reader.readToken().getLocation();

		// No further validation required on the keyword, advance by one token.
		reader.index++;

		// Use at the end of every subclass of a statement node.
		super.parse(reader);
	}
}