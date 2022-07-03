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

import { DiagnosticSeverity } from "vscode";
import { Token, TokenType } from "../../../../../lexer/tokens/Token";
import { PunctuationTypes } from "../../../../../lexer/tokens/types/Punctuation";
import { SpecialTokenTypes } from "../../../../../lexer/tokens/types/SpecialToken";
import { ScriptReader } from "../../../../logic/ScriptReader";
import { TokenRule } from "../../../../logic/TokenRule";
import { ArgumentsExpression } from "../../../expression/args/ArgumentsExpression";
import { LogicalExpression } from "../../../expression/logical/LogicalExpression";
import { IASTNode } from "../../IASTNode";

/**
 * This Macro Function Call class will only be used in the root branch of the AST.
 * The Function Call class will handle Macro & Script calls in nested branches.
 * 
 * Macro functions ideally need to be inserted before parse - this is a bad solution
 * 
 * Rule: {Name}(
 */
export class MacroFunctionCall implements IASTNode {
	functionName?: string;
	argsExpression: ArgumentsExpression = new ArgumentsExpression();

	getChildren(): IASTNode[] {
		return [];
	}

	matches(reader: ScriptReader): boolean {
		const openParen = new TokenRule(TokenType.Punctuation, PunctuationTypes.OpenParen);
		
		return (
			reader.readToken().getType() === TokenType.Name &&
			openParen.matches(reader.readAhead())
		);
	}

	parse(reader: ScriptReader, allowedChildrenFunc: (() => IASTNode[]) | undefined): void {
		// Save function name
		this.functionName = (<Token> reader.readToken()).contents;
		reader.index++;

		// Parse arguments
		try {
			// Initialise
			this.argsExpression.parse(reader);
	
			// Read argument by argument
			while(!this.argsExpression.ended) {
				const arg = new LogicalExpression();
				arg.parse(reader);
	
				this.argsExpression.arguments.push(arg);
				this.argsExpression.advance(reader);
			}
		} catch(e) {
			reader.diagnostic.pushFromError(e);
		}

		// Read a semicolon if it's there, otherwise we're done
		const semiColon = new TokenRule(TokenType.SpecialToken, SpecialTokenTypes.EndStatement);
		if(semiColon.matches(reader.readToken())) {
			reader.index++;
		}

		// Done
	}

}