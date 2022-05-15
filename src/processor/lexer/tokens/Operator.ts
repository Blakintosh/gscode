import {Token, TokenType} from "./Token";
import * as vscode from 'vscode';

/* eslint-disable @typescript-eslint/naming-convention */
/**
 * Operators that GSC supports. Ordered by char count as first match will be used
 */
enum OperatorType {
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
	Unknown = "unk"
}

/**
 * AST Class for an Operator Token
 * Structure:
 * {Operator}
 * Examples (see above)
 */
export class Operator extends Token {
	type: OperatorType;

	constructor() {
		super();
		this.type = OperatorType.Unknown;
	}

	pushSemanticTokens(builder: vscode.SemanticTokensBuilder): void {
		// Not implemented
	}
	
	getType(): TokenType {
		throw new Error("Method not implemented.");
	}
	
	/**
	 * Validates whether the next Token in the file matches this type.
	 * @param position Current base position in the file.
	 * @param prefix Prefix that may be applied to expand RegEx search in the case of a syntax error.
	 * @returns true if matches, false otherwise
	 */
	tokenMatches(inputText: String, position: number): boolean {
		throw new Error("Method not implemented.");
	}

	isBranch(): boolean {
		return false;
	}
}