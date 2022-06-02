/* eslint-disable @typescript-eslint/naming-convention */
import { Token, TokenType } from "../Token";

/**
 * Name GSC Token
 * Any reference or declaration
 */
export class Name extends Token {
	getType(): TokenType {
		return TokenType.Name;
	}

	getSpecificType(): string {
		throw new Error("Token Type has no specific type");
	}

	getRegex(): RegExp {
		return /[A-Za-z_](?:\w|\\|\.)*/;
	}
}