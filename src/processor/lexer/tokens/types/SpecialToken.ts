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
		return /^(?:0x|\[\[|\]\]|\$|#|\.|;|,|::|\\)/;
	}
}