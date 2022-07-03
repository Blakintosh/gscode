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
import { ScriptReader } from "../../../../logic/ScriptReader";
import { TokenRule } from "../../../../logic/TokenRule";
import { ForeachLoopExpression } from "../../../expression/loop/ForeachLoopExpression";
import { StatementContents } from "../../../expression/StatementContents";
import { StatementNode } from "../../StatementNode";

export class ForeachStatement extends StatementNode {
	loopExpression: ForeachLoopExpression = new ForeachLoopExpression();

	constructor() {
		super();
		// A foreach loop statement is a branching statement node
		super.expectsBranch = true;
		super.expectedChildren = GSCBranchNodes.Loop;
	}

	getContents(): StatementContents {
		throw new Error("Method not implemented.");
	}

	getRule(): TokenRule[] {
		return [
			new TokenRule(TokenType.Keyword, KeywordTypes.Foreach)
		];
	}

	parse(reader: ScriptReader): void {
		// No further validation required on the keyword, advance by one token.
		reader.index++;

		// Parse the loop expression
		try {
			this.loopExpression.parse(reader);
		} catch(e) {
			reader.diagnostic.pushFromError(e);
		}

		// Use at the end of every subclass of a statement node.
		super.parse(reader);
	}
}