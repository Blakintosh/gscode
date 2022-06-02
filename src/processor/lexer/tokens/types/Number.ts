/* eslint-disable @typescript-eslint/naming-convention */
import { Token, TokenType } from "../Token";

/**
 * String types in GSC.
 */
enum NumberTypes {
	Integer = "0",
	Float = "0.0",
}

/**
 * Punctuation GSC Token
 * For code branches, [], ().
 */
export class Number extends Token {
	type: NumberTypes = NumberTypes.Integer;

	populate(contents: string, start: number, end: number): void {
		super.populate(contents, start, end);

		if(!contents.includes(".")) {
			this.type = NumberTypes.Integer;
		} else {
			this.type = NumberTypes.Float;
		}
	}

	getType(): TokenType {
		return TokenType.Number;
	}

	getSpecificType(): NumberTypes {
		return this.type;
	}

	getRegex(): RegExp {
		return /\d*\.\d+|\d+/;
	}
}