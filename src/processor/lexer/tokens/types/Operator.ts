/* eslint-disable @typescript-eslint/naming-convention */
import { Token, TokenType } from "../Token";

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
	TernaryStart = "?",
	TernaryElse = ":",
}

/**
 * Standard GSC operator Token
 * Arithmetic and assignment operators
 * As specified in Treyarch's spec.
 */
export class Operator extends Token {
	type: string = OperatorType.And;

	populate(contents: string, start: number, end: number): void {
		super.populate(contents, start, end);

		for(const keyword in OperatorType) {
			if(keyword === contents) {
				this.type = keyword;
				break;
			}
		}
	}

	getType(): TokenType {
		return TokenType.Operator;
	}

	getSpecificType(): string {
		return this.type;
	}

	getRegex(): RegExp {
		return /<<=|>>=|!==|===|\|\||&&|==|!=|<=|>=|<<|>>|->|\+\+|--|\|=|\^=|&=|\+=|-=|\*=|\/=|%=|\||&|\^|<|>|\+|-|\*|\/|%|!|~|=|\?|:/;
	}
}