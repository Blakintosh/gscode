/* eslint-disable @typescript-eslint/naming-convention */
import { Token, TokenType } from "../Token";

/**
 * Whitespace GSC Token
 * Will be skipped over on lexical analysis
 */
export class Whitespace extends Token {
	getType(): TokenType {
		return TokenType.Whitespace;
	}

	getSpecificType(): string {
		throw new Error("Token Type has no specific type");
	}

	getRegex(): RegExp {
		return /\s+/;
	}
}