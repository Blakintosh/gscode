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
import { DataExpression } from "./DataExpression";

export class OperatorDef {
	public readonly operator: OperatorType;
	public readonly precedence: number;
	public readonly logicalType: LogicalType;

	constructor(operator: OperatorType, precedence: number, logicalType: LogicalType) {
		this.operator = operator;
		this.precedence = precedence;
		this.logicalType = logicalType;
	}
}

enum LogicalType {
	Left,
	Right,
	LeftXorRight,
	Both,
	BothOrRight,
}

/**
 * Map that stores the definitions for each operator, inc. their precedence and expected sub-expressions.
 * 0 is the highest precedence and each i above 0 is a lower level, 0 will appear lowest in the Expression AST.
 */
let ExpressionDefMap: Map<OperatorType, () => OperatorDef> = new Map<OperatorType, () => OperatorDef>([
	[OperatorType.Increment, () => new OperatorDef(OperatorType.Increment, 0, LogicalType.LeftXorRight)],
	[OperatorType.Decrement, () => new OperatorDef(OperatorType.Decrement, 0, LogicalType.LeftXorRight)],
	[OperatorType.Not, () => new OperatorDef(OperatorType.Not, 1, LogicalType.Right)],
	[OperatorType.Multiply, () => new OperatorDef(OperatorType.Multiply, 2, LogicalType.Both)],
	[OperatorType.Divide, () => new OperatorDef(OperatorType.Divide, 2, LogicalType.Both)],
	[OperatorType.Modulo, () => new OperatorDef(OperatorType.Modulo, 2, LogicalType.BothOrRight)], // Can also be %anim
	[OperatorType.Plus, () => new OperatorDef(OperatorType.Plus, 3, LogicalType.Both)],
	[OperatorType.Minus, () => new OperatorDef(OperatorType.Minus, 3, LogicalType.Both)],
	[OperatorType.BitwiseLeftShift, () => new OperatorDef(OperatorType.BitwiseLeftShift, 4, LogicalType.Both)],
	[OperatorType.BitwiseRightShift, () => new OperatorDef(OperatorType.BitwiseRightShift, 4, LogicalType.Both)],
	[OperatorType.LessThan, () => new OperatorDef(OperatorType.LessThan, 5, LogicalType.Both)],
	[OperatorType.GreaterThan, () => new OperatorDef(OperatorType.GreaterThan, 5, LogicalType.Both)],
	[OperatorType.LessThanEquals, () => new OperatorDef(OperatorType.LessThanEquals, 5, LogicalType.Both)],
	[OperatorType.GreaterThanEquals, () => new OperatorDef(OperatorType.GreaterThanEquals, 5, LogicalType.Both)],
	[OperatorType.Equals, () => new OperatorDef(OperatorType.Equals, 6, LogicalType.Both)],
	[OperatorType.NotEquals, () => new OperatorDef(OperatorType.NotEquals, 6, LogicalType.Both)],
	[OperatorType.BitwiseAnd, () => new OperatorDef(OperatorType.BitwiseAnd, 7, LogicalType.BothOrRight)], // Can also be &function
	[OperatorType.Xor, () => new OperatorDef(OperatorType.Xor, 7, LogicalType.Both)],
	[OperatorType.BitwiseOr, () => new OperatorDef(OperatorType.BitwiseOr, 7, LogicalType.Both)],
	[OperatorType.And, () => new OperatorDef(OperatorType.And, 7, LogicalType.Both)],
	[OperatorType.Or, () => new OperatorDef(OperatorType.Or, 7, LogicalType.Both)],
	[OperatorType.TernaryStart, () => new OperatorDef(OperatorType.TernaryStart, 8, LogicalType.Both)],
	[OperatorType.TernaryElse, () => new OperatorDef(OperatorType.TernaryElse, 8, LogicalType.Both)],
	[OperatorType.Assignment, () => new OperatorDef(OperatorType.Assignment, 9, LogicalType.Both)],
	[OperatorType.AssignmentPlus, () => new OperatorDef(OperatorType.AssignmentPlus, 9, LogicalType.Both)],
	[OperatorType.AssignmentMinus, () => new OperatorDef(OperatorType.AssignmentMinus, 9, LogicalType.Both)],
	[OperatorType.AssignmentMultiply, () => new OperatorDef(OperatorType.AssignmentMultiply, 9, LogicalType.Both)],
	[OperatorType.AssignmentDivide, () => new OperatorDef(OperatorType.AssignmentDivide, 9, LogicalType.Both)],
	[OperatorType.AssignmentModulo, () => new OperatorDef(OperatorType.AssignmentModulo, 9, LogicalType.Both)],
	[OperatorType.AssignmentBitwiseAnd, () => new OperatorDef(OperatorType.AssignmentBitwiseAnd, 9, LogicalType.Both)],
	[OperatorType.AssignmentBitwiseOr, () => new OperatorDef(OperatorType.AssignmentBitwiseOr, 9, LogicalType.Both)],
	[OperatorType.AssignmentBitwiseXor, () => new OperatorDef(OperatorType.AssignmentBitwiseXor, 9, LogicalType.Both)],
	[OperatorType.AssignmentBitwiseLeftShift, () => new OperatorDef(OperatorType.AssignmentBitwiseLeftShift, 9, LogicalType.Both)],
	[OperatorType.AssignmentBitwiseRightShift, () => new OperatorDef(OperatorType.AssignmentBitwiseRightShift, 9, LogicalType.Both)],
]);
/**
 * Class that stores the index and operator token of an operator in an expression for sorting
 */
export class LogicalOperator {
	readonly index: number;
	readonly def: OperatorDef;
	readonly operator: Token;

	constructor(index: number, operator: Token, def: OperatorDef) {
		this.index = index;
		this.operator = operator;
		this.def = def;
	}
}

export class LogicalExpression extends StatementContents {
	value?: DataExpression;
    operator?: OperatorType;
	left?: LogicalExpression;
	right?: LogicalExpression;
	// Logical expression will evaluate between leftBound + 1 and rightBound - 1 inclusive
	leftBound?: number;
	rightBound?: number;
	// Private scan properties
	private inParenthesis: boolean = false;
	private expressionTokens: Token[] = [];
	private operators: LogicalOperator[] = [];

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

	scan(reader: ScriptReader): void {
		const endExpressionToken = new TokenRule(TokenType.Punctuation, PunctuationTypes.CloseParen);
		const nestedExpressionToken = new TokenRule(TokenType.Punctuation, PunctuationTypes.OpenParen);
		const commaToken = new TokenRule(TokenType.SpecialToken, SpecialTokenTypes.Comma);
		const endStatementToken = new TokenRule(TokenType.SpecialToken, SpecialTokenTypes.EndStatement);

		const enterBrackets = new TokenRule(TokenType.Punctuation, PunctuationTypes.OpenBracket);
		const exitBrackets = new TokenRule(TokenType.Punctuation, PunctuationTypes.CloseBracket);

		const keywordTrue = new TokenRule(TokenType.Keyword, KeywordTypes.True);
		const keywordFalse = new TokenRule(TokenType.Keyword, KeywordTypes.False);
		const keywordUndefined = new TokenRule(TokenType.Keyword, KeywordTypes.Undefined);

		const namespaceToken = new TokenRule(TokenType.SpecialToken, SpecialTokenTypes.NamespaceCall);

		if(!this.leftBound) {
			this.leftBound = reader.index - 1;
		} else {
			reader.index = this.leftBound + 1;
		}

		let started = false;

		let token;
		while(!reader.atEof() && (token = reader.readToken()) && (
			token.getType() === TokenType.Name || // Names such as var references, func. references
			token.getType() === TokenType.Number || // Numbers
			token.getType() === TokenType.ScriptString || // Strings
			token.getType() === TokenType.Operator || // Operators (+, - etc.)
			token.getType() === TokenType.Punctuation || // Punctuation (, ) etc.
			keywordTrue.matches(token) || // Boolean true
			keywordFalse.matches(token) || // Boolean false
			keywordUndefined.matches(token) || // Undefined
			namespaceToken.matches(token) // ::
		)) {
			
			if(endExpressionToken.matches(token) || exitBrackets.matches(token) || commaToken.matches(token) || endStatementToken.matches(token) || reader.index === this.rightBound) {
				this.rightBound = reader.index;
				return;
			}

			reader.index++;
			this.inParenthesis = false;

			// This won't work for function calls (TODO) - maybe it doesn't need to?
			if(nestedExpressionToken.matches(token)) {
				if(!started) {
					this.inParenthesis = true;
				}
				this.cheapPunctuationScan(reader, PunctuationTypes.OpenParen, PunctuationTypes.CloseParen);
				continue;
			} else if(enterBrackets.matches(token)) {
				this.cheapPunctuationScan(reader, PunctuationTypes.OpenBracket, PunctuationTypes.CloseBracket);
				continue;
			}
			started = true;

			if(token.getType() === TokenType.Operator) {
				const def = ExpressionDefMap.get(<OperatorType> token.getSpecificType());
				if(def)
				{
					this.operators.push(new LogicalOperator(reader.index - 1, <Token> token, def()));
				}
			}

			if(token.getType() === TokenType.Punctuation && (token.getSpecificType() !== PunctuationTypes.OpenBracket && token.getSpecificType() !== PunctuationTypes.CloseBracket)) {
				break;
			}
			this.expressionTokens.push(<Token> token);
		}

		// Populate right bound
		this.rightBound = reader.index;

		// If there's a token remaining and it doesn't match the end of a statement, then there's a syntax error.
		if(token && !endStatementToken.matches(token) && !commaToken.matches(token)) {
			throw new ScriptError(token.getLocation(), "Unexpected token in logical expression.", GSCProcessNames.Parser);
		}
	}

	private getPrecedence(def: OperatorDef): number {
		return def.precedence;
	}

	/**
	 * Base case: there are no operators. Search for a data expression.
	 * @param reader Reference to the script reader
	 */
	private noOperators(reader: ScriptReader): void {
		if(this.leftBound) {
			reader.index = this.leftBound + 1;
		}

		if(!this.inParenthesis) {
			this.value = new DataExpression();
			this.value.parse(reader);
		} else if(this.leftBound && this.rightBound) {
			this.leftBound++;
			this.rightBound--;

			const leftBoundToken = reader.readToken();
			const rightBoundToken = reader.readToken(this.rightBound - this.leftBound);

			const openParen = new TokenRule(TokenType.Punctuation, PunctuationTypes.OpenParen);
			const closeParen = new TokenRule(TokenType.Punctuation, PunctuationTypes.CloseParen);

			if(!openParen.matches(leftBoundToken) || !closeParen.matches(rightBoundToken) || this.rightBound - this.leftBound < 0) {
				throw new ScriptError([leftBoundToken.getLocation()[0], rightBoundToken.getLocation()[1]], "Logical expression expected.", GSCProcessNames.Parser);
			} else {
				this.parse(reader);
			}
		}
	}

	/**
	 * Recursive case: there are operators. Recursively divide into nested expressions.
	 * @param reader Reference to the script reader
	 */
	private hasOperators(reader: ScriptReader): void {
		try {
			const lowestPrecedenceOperator = this.operators[0];
	
			const left = this.leftBound;
			const pivot = lowestPrecedenceOperator.index;
			const right = this.rightBound;
	
			if(!this.leftBound || !this.rightBound) {
				const opLoc = lowestPrecedenceOperator.operator.getLocation();
				throw new ScriptError([opLoc[0], opLoc[1]], "Logical expression bounds undefined.", GSCProcessNames.Parser);
			}
	
			this.left = new LogicalExpression();
			this.left.leftBound = left;
			this.left.rightBound = pivot;
	
			this.right = new LogicalExpression();
			this.right.leftBound = pivot;
			this.right.rightBound = right;

			const logicalType = lowestPrecedenceOperator.def.logicalType;

			let leftUsed = false;
			let rightUsed = false;

			const startLoc = reader.index;
	
			if(logicalType !== LogicalType.Right) {
				try {
					this.left.parse(reader);
					leftUsed = true;
				} catch(e) {
					if(logicalType !== LogicalType.LeftXorRight && logicalType !== LogicalType.BothOrRight) {
						reader.diagnostic.pushFromError(e);
					}
				} finally {
					if(this.left.expressionTokens.length === 0) {
						leftUsed = false;
						this.left = undefined;
					}
				}
			}

			reader.index = startLoc;
	
			if(logicalType !== LogicalType.Left && (!leftUsed || logicalType !== LogicalType.LeftXorRight)) {
				try {
					this.right.parse(reader);
					rightUsed = true;
				} catch(e) {
					reader.diagnostic.pushFromError(e);
				}
			} else {
				reader.index = pivot + 1;
			}
		} catch(e) {
			reader.diagnostic.pushFromError(e);
		}
	}

    /**
     * Parses the given statement contents, which may include recursive calls.
     * Logical expressions will recurse through nested expressions until reaching base data.
     */
    parse(reader: ScriptReader): void {
		// Scan for the operators and tokens in this branch
		this.scan(reader);

		if(this.operators.length === 0) {
			this.noOperators(reader);
		} else {
			// Sort operators by precedence
			this.operators.sort((a, b) => {
				let precedenceA = this.getPrecedence(a.def);
				let precedenceB = this.getPrecedence(b.def);
	
				if(precedenceA > precedenceB) {
					return -1;
				} else if(precedenceA < precedenceB) {
					return 1;
				} else {
					return a.index - b.index;
				}
			});

			this.hasOperators(reader);
		}

		// Done
    }
}