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
import { GSCBranchNodes, GSCProcessNames } from "../../../../../util/GSCUtil";
import { ScriptReader } from "../../../../logic/ScriptReader";
import { TokenRule } from "../../../../logic/TokenRule";
import { StatementContents } from "../../../expression/StatementContents";
import { ParenBooleanExpression } from "../../../expression/types/ParenBooleanExpression";
import { StatementNode } from "../../StatementNode";

export class IfStatement extends StatementNode {
	booleanExpression: ParenBooleanExpression = new ParenBooleanExpression();

	constructor() {
		super();
		// A function declaration is a branching statement node
		super.expectsBranch = true;
		super.expectedChildren = GSCBranchNodes.Standard;
	}

    getContents(): StatementContents {
        throw new Error("Method not implemented.");
    }

    getRule(): TokenRule[] {
        return [
            new TokenRule(TokenType.Keyword, KeywordTypes.If)
        ];
    }

	parse(reader: ScriptReader): void {

		// No further validation required on the keyword, advance by one token.
		reader.index++;

		// Check we have an open parenthesis
		const openParen = new TokenRule(TokenType.Punctuation, PunctuationTypes.OpenParen);
		if(!openParen.matches(reader.readToken())) {
			reader.diagnostic.pushDiagnostic(reader.readToken(-1).getLocation(), "Expected '('", GSCProcessNames.Parser);
		} else {
			reader.index++;
		}

		// Parse the condition expression
		try {
			this.booleanExpression.parse(reader);
		} catch(e) {
			reader.diagnostic.pushFromError(e);
		}

		// Use at the end of every subclass of a statement node.
		super.parse(reader);
	}
}