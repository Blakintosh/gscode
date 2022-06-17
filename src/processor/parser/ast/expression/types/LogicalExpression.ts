/* eslint-disable @typescript-eslint/naming-convention */
import { Token, TokenType } from "../../../../lexer/tokens/Token";
import { ScriptReader } from "../../../logic/ScriptReader";
import { StatementContents } from "../StatementContents";
import * as vscode from "vscode";
import { GSCUtil } from "../../../../util/GSCUtil";
import { ScriptDependency } from "../../../data/ScriptDependency";
import { TokenRule } from "../../../logic/TokenRule";
import { OperatorType } from "../../../../lexer/tokens/types/Operator";
import { SpecialTokenTypes } from "../../../../lexer/tokens/types/SpecialToken";
import { PunctuationTypes } from "../../../../lexer/tokens/types/Punctuation";
import { IToken } from "../../../../lexer/tokens/IToken";

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

	cheapParenthesisScan(reader: ScriptReader): void {
		// Scan until parenthesis count falls to 0.
		let parenthesisCount = 1;

		let open = new TokenRule(TokenType.Punctuation, PunctuationTypes.OpenParen);
		let close = new TokenRule(TokenType.Punctuation, PunctuationTypes.CloseParen);

		while (!reader.atEof() && parenthesisCount > 0) {
			let token = reader.readToken();

			if(open.matches(token)) {
				parenthesisCount++;
			} else if(close.matches(token)) {
				parenthesisCount--;
			}
			reader.index++;
		}
	}

	scan(reader: ScriptReader): Token[] {
		let expressionTokens: Token[] = [];

		let endExpressionToken = new TokenRule(TokenType.Punctuation, PunctuationTypes.CloseParen);
		let nestedExpressionToken = new TokenRule(TokenType.Punctuation, PunctuationTypes.OpenParen);

		let token;
		while(!reader.atEof() && (
			reader.readToken().getType() === TokenType.Name ||
			reader.readToken().getType() === TokenType.Number ||
			reader.readToken().getType() === TokenType.ScriptString ||
			reader.readToken().getType() === TokenType.Operator ||
			reader.readToken().getType() === TokenType.Punctuation
		)) {
			token = reader.readToken();
			reader.index++;

			if(endExpressionToken.matches(token)) {
				return expressionTokens;
			}

			if(nestedExpressionToken.matches(token)) {
				this.cheapParenthesisScan(reader);
				continue;
			}

			if(token.getType() === TokenType.Punctuation && (token.getSpecificType() !== PunctuationTypes.OpenBracket && token.getSpecificType() !== PunctuationTypes.CloseBracket)) {
				break;
			}
			expressionTokens.push(<Token> token);
		}

		if(token) {
			reader.diagnostic.pushDiagnostic(token.getLocation(), "Unexpected token in logical expression.");
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