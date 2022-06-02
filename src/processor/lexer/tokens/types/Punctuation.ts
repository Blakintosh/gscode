/* eslint-disable @typescript-eslint/naming-convention */
import { Token, TokenType } from "../Token";

/**
 * Punctuation types in GSC.
 */
enum PunctuationTypes {
	OpenBrace = "{",
	CloseBrace = "}",
	OpenBracket = "[",
	CloseBracket = "]",
	OpenParen = "(",
	CloseParen = ")"
}

/**
 * Punctuation GSC Token
 * For code branches, [], ().
 */
export class Punctuation extends Token {
	type: string = PunctuationTypes.OpenBrace;

	populate(contents: string, start: number, end: number): void {
		super.populate(contents, start, end);

		for(const keyword in PunctuationTypes) {
			if(keyword === contents) {
				this.type = keyword;
				break;
			}
		}
	}

	getType(): TokenType {
		return TokenType.Punctuation;
	}

	getSpecificType(): string {
		return this.type;
	}

	getRegex(): RegExp {
		return /{|}|\[|\]|\(|\)/;
	}
}