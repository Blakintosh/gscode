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
		return /^(?:<<=|>>=|!==|===|\|\||&&|==|!=|<=|>=|<<|>>|->|\+\+|--|\|=|\^=|&=|\+=|-=|\*=|\/=|%=|\||&|\^|<|>|\+|-|\*|\/|%|!|~|=|\?|:)/;
	}
}