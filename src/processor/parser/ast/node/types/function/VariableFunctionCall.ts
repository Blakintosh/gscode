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
import { IToken } from "../../../../../lexer/tokens/IToken";
import { Token, TokenType } from "../../../../../lexer/tokens/Token";
import { KeywordTypes } from "../../../../../lexer/tokens/types/Keyword";
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
 * [Name] [thread] [Namespace::]{FunctionName} ([Arguments]);
 */

export class DirectFunctionCall implements IASTNode {
	argumentsExpression: ArgumentsExpression = new ArgumentsExpression();

	namespace?: Token;
	calledOn?: Token;
	name?: Token;
	threaded: boolean = false;

	getChildren(): IASTNode[] {
		throw new Error("Method not implemented.");
	}

	/**
	 * Returns true if the next token suggests a called on entity
	 * @param reader Reference to the reader
	 * @returns true if matches, false otherwise
	 */
	private calledOnMatches(reader: ScriptReader): boolean {
		const token = reader.readToken();
		const nextToken = reader.readAhead();

		const openParenRule = new TokenRule(TokenType.Punctuation, PunctuationTypes.OpenParen);
		const namespaceRule = new TokenRule(TokenType.SpecialToken, SpecialTokenTypes.NamespaceCall);
		if(token.getType() === TokenType.Name && !namespaceRule.matches(nextToken) && !openParenRule.matches(nextToken)) {
			return true;
		}
		return false;
	}

	/**
	 * Returns true if the next token suggests a namespace
	 * @param reader Reference to the reader
	 * @returns true if matches, false otherwise
	 */
	private namespaceMatches(reader: ScriptReader): boolean {
		const token = reader.readToken();
		const nextToken = reader.readAhead();

		const namespaceRule = new TokenRule(TokenType.SpecialToken, SpecialTokenTypes.NamespaceCall);
		if(token.getType() === TokenType.Name && namespaceRule.matches(nextToken)) {
			reader.index++;
			return true;
		}
		return false;
	}

	/**
	 * Returns true if the next token suggests a function name
	 * @param reader Reference to the reader
	 * @returns true if matches, false otherwise
	 */
	private functionNameMatches(reader: ScriptReader): boolean {
		const token = reader.readToken();
		const nextToken = reader.readAhead();

		const openParenRule = new TokenRule(TokenType.Punctuation, PunctuationTypes.OpenParen);
		if(token.getType() === TokenType.Name && openParenRule.matches(nextToken)) {
			return true;
		}
		return false;
	}

	/**
	 * Returns true if the next token is thread
	 * @param reader Reference to the reader
	 * @returns true if matches, false otherwise
	 */
	private threadMatches(reader: ScriptReader): boolean {
		const token = reader.readToken();

		const threadRule = new TokenRule(TokenType.Keyword, KeywordTypes.Thread);
		if(threadRule.matches(token)) {
			return true;
		}
		return false;
	}

	/**
	 * Attempts to parse the next tokens as a variable assignment - if it fails, then there is no match
	 * @param reader Reference to the reader
	 * @returns true if matches, false otherwise
	 */
	matches(reader: ScriptReader): boolean {
		const baseIndex = reader.index;
		
		let token = reader.readToken();

		let checkForCalledOn = true;
		let checkForThread = true;
		let checkForNamespace = true;
		let checkForFunctionName = true;

		// Continuously search for the next function match token in order
		while(checkForFunctionName && !reader.atEof()) {
			if(checkForCalledOn && this.calledOnMatches(reader)) {
				checkForCalledOn = false;
				this.calledOn = <Token> reader.readToken();
			} else if(checkForThread && this.threadMatches(reader)) {
				checkForCalledOn = false;
				checkForThread = false;
				this.threaded = true;
			} else if(checkForNamespace && this.namespaceMatches(reader)) {
				checkForCalledOn = false;
				checkForThread = false;
				checkForNamespace = false;
				this.namespace = <Token> reader.readToken();
			} else if(checkForFunctionName && this.functionNameMatches(reader)) {
				checkForCalledOn = false;
				checkForThread = false;
				checkForNamespace = false;
				checkForFunctionName = false;
				this.name = <Token> reader.readToken();
			} else {
				reader.index = baseIndex;
				return false;
			}
			reader.index++;
		}

		if(reader.atEof()) {
			return false;
		}

		const endStatementRule = new TokenRule(TokenType.SpecialToken, SpecialTokenTypes.EndStatement);

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