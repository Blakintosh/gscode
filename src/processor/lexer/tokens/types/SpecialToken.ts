/* eslint-disable @typescript-eslint/naming-convention */
import { Token, TokenType } from "../Token";

/**
 * Special Tokens in GSC. Ordered by char count as first match will be used
 */
 enum SpecialTokenTypes {
	Hex = "0x",
	StartVariableFunctionCall = "[[",
	EndVariableFunctionCall = "]]",
	Dollar = "$",
	CompileTimeHash = "#",
	Accessor = ".",
	EndStatement = ";",
	Comma = ",",
	NamespaceCall = "::",
}

/**
 * Special GSC Token
 * Accessing methods, comments, etc.
 * According to Treyarch spec., except () and [] have been omitted as they are in Punctuation
 */
export class SpecialToken extends Token {
	type: string = SpecialTokenTypes.Hex;

	populate(contents: string, start: number, end: number): void {
		super.populate(contents, start, end);

		for(const keyword in SpecialTokenTypes) {
			if(keyword === contents) {
				this.type = keyword;
				break;
			}
		}
	}

	getType(): TokenType {
		return TokenType.SpecialToken;
	}

	getSpecificType(): string {
		return this.type;
	}

	getRegex(): RegExp {
		return /0x|\[\[|\]\]|\$|#|\.|;|,|::|\\/;
	}
}