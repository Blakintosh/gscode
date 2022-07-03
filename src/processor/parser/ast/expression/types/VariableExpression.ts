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

import { Token, TokenType } from "../../../../lexer/tokens/Token";
import { PunctuationTypes } from "../../../../lexer/tokens/types/Punctuation";
import { GSCProcessNames } from "../../../../util/GSCUtil";
import { ScriptReader } from "../../../logic/ScriptReader";
import { TokenRule } from "../../../logic/TokenRule";
import { StatementContents } from "../StatementContents";
import { LogicalExpression } from "../logical/LogicalExpression";
import { SpecialTokenTypes } from "../../../../lexer/tokens/types/SpecialToken";
import { NumberTypes } from "../../../../lexer/tokens/types/Number";
import { ArgumentsExpression } from "../args/ArgumentsExpression";
import { ScriptError } from "../../../diagnostics/ScriptError";
import { OperatorType } from "../../../../lexer/tokens/types/Operator";

/**
 * Stores each individual component of a variable expression,
 * e.g. level.foo[1] is split into:
 * 0: level 1: foo[1] as VariableProperty
 * each is then pushed to the VariableExpression
 */
export class VariableProperty {
	// Target name of a property, e.g. level
	target: string;
	// Whether this property is coming from a function, e.g. GetPlayers(), if so a populated arguments expression
	isFunction: boolean;
	isArrayKey: boolean;
	args?: ArgumentsExpression;
	// If accessing an array key, index or string key. Both should not be set simultaneously
	key?: LogicalExpression;

	constructor(target: string, args?: ArgumentsExpression, key?: LogicalExpression) {
		this.target = target;

		this.args = args;
		this.key = key;
		this.isFunction = args !== undefined;
		this.isArrayKey = key !== undefined;
	}
}

/**
 * Variable expression:
 * Parses any 1+ sequence, split by ., of Name/Name() [Number/ScriptString]
 * e.g. level.foo, foo(), level._players[0], self.foo.bar, object.getName(), ducks["mallard"], foo::bar()["yeh"], ducks[foo()]
 */
export class VariableExpression extends StatementContents {
	// If first part of sequence has a namespace, specify it here
	namespace?: string;
	// Each VariableProperty in this sequence
	components: VariableProperty[] = [];

	private nextTokenIsAccessor(reader: ScriptReader): boolean {
		const dot = new TokenRule(TokenType.SpecialToken, SpecialTokenTypes.Accessor);
		return dot.matches(reader.readToken());
	}

	private nextTokenIsAssignment(reader: ScriptReader): boolean {
		const equals = new TokenRule(TokenType.Operator, OperatorType.Assignment);
		return equals.matches(reader.readToken());
	}

	private nextTokenIsCloseParen(reader: ScriptReader): boolean {
		const closeParen = new TokenRule(TokenType.Punctuation, PunctuationTypes.CloseParen);
		return closeParen.matches(reader.readToken());
	}

	private parseFunctionCall(reader: ScriptReader, args: ArgumentsExpression): void {
		// Parse arguments
		// Initialise
		args.parse(reader);

		// Read argument by argument
		while(!args.ended) {
			const arg = new LogicalExpression();
			arg.parse(reader);

			args.arguments.push(arg);
			args.advance(reader);
		}
	}

	private parseArrayKey(reader: ScriptReader): LogicalExpression {
		// Open bracket already scanned
		reader.index++;

		// Parse logical expression inside array brackets
		const expr = new LogicalExpression();
		expr.parse(reader);

		// Scan close bracket
		const closeBracket = new TokenRule(TokenType.Punctuation, PunctuationTypes.CloseBracket);

		const endToken = reader.readToken();
		reader.index++;

		if(!closeBracket.matches(endToken)) {
			throw new ScriptError(endToken.getLocation(), "Expected ']'", GSCProcessNames.Parser);
		}

		return expr;
	}
	
	/**
	 * Parses the given statement contents, which may include recursive calls.
	 * Variable expressions recurse into array keys and function calls.
	 */
	parse(reader: ScriptReader): void {
		// Types that will be checked
		const openParen = new TokenRule(TokenType.Punctuation, PunctuationTypes.OpenParen);
		const openBracket = new TokenRule(TokenType.Punctuation, PunctuationTypes.OpenBracket);

		// Check for a namespace
		const firstToken = reader.readToken();
		const namespaceCall = new TokenRule(TokenType.SpecialToken, SpecialTokenTypes.NamespaceCall);

		if(firstToken.getType() === TokenType.Name && namespaceCall.matches(reader.readAhead())) {
			this.namespace = (<Token> firstToken).contents;
			reader.index += 2;
		}

		do {
			// Attempt to parse a name
			const componentName = <Token> reader.readToken();
			reader.index++;

			if(componentName.getType() !== TokenType.Name) {
				// Error - not a name
				throw new ScriptError(componentName.getLocation(), "Expected name.", GSCProcessNames.Parser);
			}

			// Now look for an array key or function call
			if(!this.nextTokenIsAccessor(reader) && !this.nextTokenIsAssignment(reader) && !this.nextTokenIsCloseParen(reader)) {
				const puncToken = reader.readToken();
				if(openParen.matches(puncToken)) {
					// Function call
					const args = new ArgumentsExpression();

					this.parseFunctionCall(reader, args);

					// Push this to the component array
					this.components.push(new VariableProperty(componentName.contents, args));
				} else if(openBracket.matches(puncToken)) {
					// Array key
					const key = this.parseArrayKey(reader);

					// Push this to the component array
					this.components.push(new VariableProperty(componentName.contents, undefined, key));
				} else {
					throw new ScriptError(componentName.getLocation(), "Expected '(' or '['.", GSCProcessNames.Parser);
				}
			}
		} while( this.nextTokenIsAccessor(reader) && reader.index++ && !reader.atEof());
	}
}