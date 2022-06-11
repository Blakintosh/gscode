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
 * Punctuation types in GSC.
 */
export enum PunctuationTypes {
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

		for(const keyword in Object.values(PunctuationTypes)) {
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
		return /(?:{|}|\[|\]|\(|\))/;
	}
}