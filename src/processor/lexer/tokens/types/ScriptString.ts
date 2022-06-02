/* eslint-disable @typescript-eslint/naming-convention */
import { Token, TokenType } from "../Token";

/**
 * String types in GSC.
 */
enum StringTypes {
	SingleQuote = "'",
	DoubleQuote = "\"",
}

/**
 * Script String GSC Token
 * For "" and ''
 */
export class ScriptString extends Token {
	type: StringTypes = StringTypes.DoubleQuote;

	populate(contents: string, start: number, end: number): void {
		super.populate(contents, start, end);

		if(!contents.startsWith("'")) {
			this.type = StringTypes.SingleQuote;
		} else {
			this.type = StringTypes.DoubleQuote;
		}
	}

	getType(): TokenType {
		return TokenType.ScriptString;
	}

	getSpecificType(): string {
		return this.type;
	}

	getRegex(): RegExp {
		return /".*?(?<!\\)"|'.*?(?<!\\)'/;
	}
}