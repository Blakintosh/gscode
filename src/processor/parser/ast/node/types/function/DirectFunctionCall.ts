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
	import { OperatorType } from "../../../../../lexer/tokens/types/Operator";
import { PunctuationTypes } from "../../../../../lexer/tokens/types/Punctuation";
	import { SpecialTokenTypes } from "../../../../../lexer/tokens/types/SpecialToken";
	import { GSCProcessNames } from "../../../../../util/GSCUtil";
	import { ScriptReader } from "../../../../logic/ScriptReader";
	import { TokenRule } from "../../../../logic/TokenRule";
import { ArgumentsExpression } from "../../../expression/args/ArgumentsExpression";
	import { LogicalExpression } from "../../../expression/logical/LogicalExpression";
	import { VariableExpression } from "../../../expression/types/VariableExpression";
	import { IASTNode } from "../../IASTNode";
	
	/**
	 * DirectFunctionCall Syntax
	 * [Name] [Namespace::]{FunctionName} ([Arguments]);
	 */
	
	export class DirectFunctionCall implements IASTNode {
		argumentsExpression: ArgumentsExpression = new ArgumentsExpression();

		namespace?: Token;
		calledOn?: Token;
		name?: Token;
	
		getChildren(): IASTNode[] {
			throw new Error("Method not implemented.");
		}
	
		/**
		 * Attempts to parse the next tokens as a variable assignment - if it fails, then there is no match
		 * @param reader Reference to the reader
		 * @returns true if matches, false otherwise
		 */
		matches(reader: ScriptReader): boolean {
			if(reader.wouldBeAtEof(2)) {
				return false;
			}
			const firstToken = reader.readToken();
			const secondToken = reader.readAhead();
			const thirdToken = reader.readToken(2);

			const baseIndex = reader.index;

			let offset = 0;

			const namespaceRule = new TokenRule(TokenType.SpecialToken, SpecialTokenTypes.NamespaceCall);
			const openParenRule = new TokenRule(TokenType.Punctuation, PunctuationTypes.OpenParen);
			const closeParenRule = new TokenRule(TokenType.Punctuation, PunctuationTypes.CloseParen);
			const endStatementRule = new TokenRule(TokenType.SpecialToken, SpecialTokenTypes.EndStatement);

			if(firstToken.getType() === TokenType.Name) {
				if(secondToken.getType() === TokenType.Name) {
					if(namespaceRule.matches(thirdToken)) {
						this.namespace = <Token> secondToken;
						this.calledOn = <Token> firstToken;
						offset = 3;
					} else if(openParenRule.matches(thirdToken)) {
						this.calledOn = <Token> firstToken;
						this.name = <Token> secondToken;
					}
				} else if(namespaceRule.matches(secondToken)) {
					this.namespace = <Token> firstToken;
					offset = 2;
				} else if(openParenRule.matches(secondToken)) {
					this.name = <Token> firstToken;
				} else {
					return false;
				}
			} else {
				return false;
			}

			if(reader.wouldBeAtEof(offset + 1)) {
				return false;
			}

			if(!this.name) {
				const token = reader.readToken(offset);
				const nextToken = reader.readToken(offset + 1);

				if(token.getType() === TokenType.Name && openParenRule.matches(nextToken)) {
					this.name = <Token> token;
				} else {
					return false;
				}
			}

			reader.index = baseIndex + offset + 1;

			try {
				// Parse arguments
				// Initialise
				this.argumentsExpression.parse(reader);

				// Read argument by argument
				while(!this.argumentsExpression.ended) {
					const arg = new LogicalExpression();
					arg.parse(reader);

					this.argumentsExpression.arguments.push(arg);
					this.argumentsExpression.advance(reader);
				}
			} catch(e) {
				reader.index = baseIndex;
				return false;
			}
	
			if(endStatementRule.matches(reader.readToken())) {
				reader.index += 1;
				return true;
			} else {
				reader.index = baseIndex;
				return false;
			}
		}
	
		parse(reader: ScriptReader, allowedChildrenFunc: (() => IASTNode[]) | undefined): void {
			// We've already parsed this to match. Done.
		}
	}