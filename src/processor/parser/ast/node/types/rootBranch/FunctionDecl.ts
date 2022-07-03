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
import { KeywordTypes } from "../../../../../lexer/tokens/types/Keyword";
import { GSCBranchNodes, GSCProcessNames } from "../../../../../util/GSCUtil";
import { ScriptReader } from "../../../../logic/ScriptReader";
import { TokenRule } from "../../../../logic/TokenRule";
import { ArgumentsExpression } from "../../../expression/args/ArgumentsExpression";
import { StatementContents } from "../../../expression/StatementContents";
import { FunctionDeclArgExpression } from "../../../expression/types/FunctionDeclArgExpression";
import { FunctionDeclArgsExpression } from "../../../expression/types/FunctionDeclArgsExpression";
import { StatementNode } from "../../StatementNode";

export class FunctionDecl extends StatementNode {
	arguments: ArgumentsExpression = new ArgumentsExpression();
	autoexec: boolean = false;
	private: boolean = false;
	name: string = "";

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
            new TokenRule(TokenType.Keyword, KeywordTypes.Function),
			new TokenRule(TokenType.Keyword, KeywordTypes.Autoexec, true),
			new TokenRule(TokenType.Keyword, KeywordTypes.Private, true),
			new TokenRule(TokenType.Name)
        ];
    }

	parse(reader: ScriptReader): void {

		// Store keyword position
		let keywordPosition = reader.readToken().getLocation();

		// No further validation required on the keyword, advance by one token.
		reader.index++;

		// Check for autoexec, private, or both
		const autoexecRule = new TokenRule(TokenType.Keyword, KeywordTypes.Autoexec);
		const privateRule = new TokenRule(TokenType.Keyword, KeywordTypes.Private);

		let token = reader.readToken();

		if(autoexecRule.matches(token)) {
			this.autoexec = true;
			reader.index++;
		} else if(privateRule.matches(token)) {
			this.private = true;
			reader.index++;
		} else if(reader.readAhead().getType() === TokenType.Name) {
			reader.diagnostic.pushDiagnostic(token.getLocation(), "Invalid modifier.", GSCProcessNames.Parser);
			reader.index++;
		}

		token = reader.readToken();

		if(privateRule.matches(token) && !this.private) {
			this.private = true;
			reader.index++;
		}

		// Validate the function name and add it to the function names list.
		token = reader.readToken();

		this.name = (<Token> token).contents;
		reader.index++;

		// Parse the function arguments expression.
		try {
			// Parse arguments
			// Initialise
			this.arguments.parse(reader);

			// Read argument by argument
			while(!this.arguments.ended) {
				const arg = new FunctionDeclArgExpression();
				arg.parse(reader);

				this.arguments.arguments.push(arg);
				this.arguments.advance(reader);
			}
		} catch(e) {
			reader.diagnostic.pushFromError(e);
		}

		// Use at the end of every subclass of a statement node.
		super.parse(reader);
	}
}