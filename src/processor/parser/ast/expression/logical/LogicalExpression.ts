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

/* eslint-disable @typescript-eslint/naming-convention */
import { Token, TokenType } from "../../../../lexer/tokens/Token";
import { KeywordTypes } from "../../../../lexer/tokens/types/Keyword";
import { OperatorType } from "../../../../lexer/tokens/types/Operator";
import { PunctuationTypes } from "../../../../lexer/tokens/types/Punctuation";
import { SpecialTokenTypes } from "../../../../lexer/tokens/types/SpecialToken";
import { GSCProcessNames } from "../../../../util/GSCUtil";
import { ScriptError } from "../../../diagnostics/ScriptError";
import { ScriptReader } from "../../../logic/ScriptReader";
import { TokenRule } from "../../../logic/TokenRule";
import { StatementContents } from "../StatementContents";

// Expression types in GSC, includes data.
/*export enum ExpressionType {
	// 3
	AssignmentBitwiseLeftShift = "<<=",
	AssignmentBitwiseRightShift = ">>=",
	NotTypeEquals = "!==",
	TypeEquals = "===",
	// 2
	And = "&&",
	AssignmentBitwiseAnd = "&=",
	AssignmentBitwiseOr = "|=",
	AssignmentBitwiseXor = "^=",
	AssignmentDivide = "/=",
	AssignmentMinus = "-=",
	AssignmentModulo = "%=",
	AssignmentMultiply = "*=",
	AssignmentPlus = "+=",
	BitwiseLeftShift = "<<",
	BitwiseRightShift = ">>",
	Decrement = "--",
	Equals = "==",
	GreaterThanEquals = ">=",
	Increment = "++",
	LessThanEquals = "<=",
	NotEquals = "!=",
	Or = "||",
	PointerMethod = "->",
	// 1
	Assignment = "=",
	BitwiseAnd = "&",
	BitwiseNot = "~",
	BitwiseOr = "|",
	Divide = "/",
	GreaterThan = ">",
	LessThan = "<",
	Minus = "-",
	Modulo = "%",
	Multiply = "*",
	Not = "!",
	Plus = "+",
	Xor = "^",
	TernaryStart = "?",
	TernaryElse = ":",
}*/

/**
 * 2D Array that stores the precedence of each operator.
 * It is in descending order, such that the first index is the highest precedence, and will appear lowest in the Expression AST.
 * This is approximately based on Java's order of operations.
 */
let ExpressionOperatorPrecedence = [
	[
		OperatorType.Increment, // does not distinguish between postfix and prefix, if this is an issue specific logic might be needed
		OperatorType.Decrement
	],
	[
		OperatorType.Not
	],
	[
		OperatorType.Multiply,
		OperatorType.Divide,
		OperatorType.Modulo
	],
	[
		OperatorType.Plus,
		OperatorType.Minus
	],
	[
		OperatorType.BitwiseLeftShift,
		OperatorType.BitwiseRightShift
	],
	[
		OperatorType.LessThan,
		OperatorType.GreaterThan,
		OperatorType.LessThanEquals,
		OperatorType.GreaterThanEquals
	],
	[
		OperatorType.Equals,
		OperatorType.NotEquals
	],
	[ OperatorType.BitwiseAnd ],
	[ OperatorType.Xor ],
	[ OperatorType.BitwiseOr ],
	[ OperatorType.And ],
	[ OperatorType.Or ],
	[
		OperatorType.TernaryStart,
		OperatorType.TernaryElse
	],
	[
		OperatorType.Assignment, 
		OperatorType.AssignmentPlus, 
		OperatorType.AssignmentMinus, 
		OperatorType.AssignmentMultiply, 
		OperatorType.AssignmentDivide, 
		OperatorType.AssignmentModulo, 
		OperatorType.AssignmentBitwiseAnd, 
		OperatorType.AssignmentBitwiseOr, 
		OperatorType.AssignmentBitwiseXor, 
		OperatorType.AssignmentBitwiseLeftShift, 
		OperatorType.AssignmentBitwiseRightShift
	],
];

export class LogicalExpression extends StatementContents {
	value?: string;
    operator?: OperatorType;
	left?: LogicalExpression;
	right?: LogicalExpression;
	// Logical expression will evaluate between leftBound + 1 and rightBound - 1 inclusive
	leftBound?: number;
	rightBound?: number;

	cheapPunctuationScan(reader: ScriptReader, openType: PunctuationTypes, closeType: PunctuationTypes): void {
		// Scan until parenthesis count falls to 0.
		let puncCount = 1;

		let open = new TokenRule(TokenType.Punctuation, openType);
		let close = new TokenRule(TokenType.Punctuation, closeType);

		while (!reader.atEof() && puncCount > 0) {
			let token = reader.readToken();

			if(open.matches(token)) {
				puncCount++;
			} else if(close.matches(token)) {
				puncCount--;
			}
			reader.index++;
		}
	}

	scan(reader: ScriptReader): Token[] {
		let expressionTokens: Token[] = [];

		const endExpressionToken = new TokenRule(TokenType.Punctuation, PunctuationTypes.CloseParen);
		const nestedExpressionToken = new TokenRule(TokenType.Punctuation, PunctuationTypes.OpenParen);
		const commaToken = new TokenRule(TokenType.SpecialToken, SpecialTokenTypes.Comma);
		const endStatementToken = new TokenRule(TokenType.SpecialToken, SpecialTokenTypes.EndStatement);

		const enterBrackets = new TokenRule(TokenType.Punctuation, PunctuationTypes.OpenBracket);
		const exitBrackets = new TokenRule(TokenType.Punctuation, PunctuationTypes.CloseBracket);

		const keywordTrue = new TokenRule(TokenType.Keyword, KeywordTypes.True);
		const keywordFalse = new TokenRule(TokenType.Keyword, KeywordTypes.False);
		const keywordUndefined = new TokenRule(TokenType.Keyword, KeywordTypes.Undefined);

		let token;
		while(!reader.atEof() && (token = reader.readToken()) && (
			token.getType() === TokenType.Name || // Names such as var references, func. references
			token.getType() === TokenType.Number || // Numbers
			token.getType() === TokenType.ScriptString || // Strings
			token.getType() === TokenType.Operator || // Operators (+, - etc.)
			token.getType() === TokenType.Punctuation || // Punctuation (, ) etc.
			keywordTrue.matches(token) || // Boolean true
			keywordFalse.matches(token) || // Boolean false
			keywordUndefined.matches(token) // Undefined
		)) {
			
			if(endExpressionToken.matches(token) || exitBrackets.matches(token) || commaToken.matches(token) || endStatementToken.matches(token)) {
				return expressionTokens;
			}
			reader.index++;

			// This won't work for function calls (TODO) - maybe it doesn't need to?
			if(nestedExpressionToken.matches(token)) {
				this.cheapPunctuationScan(reader, PunctuationTypes.OpenParen, PunctuationTypes.CloseParen);
				continue;
			} else if(enterBrackets.matches(token)) {
				this.cheapPunctuationScan(reader, PunctuationTypes.OpenBracket, PunctuationTypes.CloseBracket);
				continue;
			}

			if(token.getType() === TokenType.Punctuation && (token.getSpecificType() !== PunctuationTypes.OpenBracket && token.getSpecificType() !== PunctuationTypes.CloseBracket)) {
				break;
			}
			expressionTokens.push(<Token> token);
		}

		// If there's a token remaining and it doesn't match the end of a statement, then there's a syntax error.
		if(token && !endStatementToken.matches(token) && !commaToken.matches(token)) {
			throw new ScriptError(token.getLocation(), "Unexpected token in logical expression.", GSCProcessNames.Parser);
		}

		return expressionTokens;
	}

	private getPrecedence(type: string): [number, number] {
		for(let i = 0; i < ExpressionOperatorPrecedence.length; i++) {
			for(let j = 0; j < ExpressionOperatorPrecedence[i].length; j++) {
				if(ExpressionOperatorPrecedence[i][j] === type) {
					return [i, j];
				}
			}
		}
		return [0, 0];
	}

    /**
     * Parses the given statement contents, which may include recursive calls.
     * Logical expressions will recurse through nested expressions until reaching base data.
     */
    parse(reader: ScriptReader): void {
		// Scan for the operators and tokens in this branch
		let tokens = this.scan(reader);
		let operators: Token[] = [];

		for(let token of tokens) {
			if(token.getType() === TokenType.Operator) {
				operators.push(token);
			}
		}

		// Sort operators by precedence
		operators.sort((a, b) => {
			let precedenceA = this.getPrecedence(a.getSpecificType());
			let precedenceB = this.getPrecedence(b.getSpecificType());

			if(precedenceA[0] > precedenceB[0]) {
				return 1;
			} else if(precedenceA[0] < precedenceB[0]) {
				return -1;
			} else {
				return precedenceA[1] - precedenceB[1];
			}
		});

		// Done
    }
}