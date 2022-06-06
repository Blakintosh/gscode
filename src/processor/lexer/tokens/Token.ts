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
import { IToken } from './IToken';

/**
 * Enum for all the possible token types
 * All derivatives of Token & IToken should uniquely match to one of these
 */
export enum TokenType {
	Unknown,
	Keyword,
	Name,
	Operator,
	Punctuation,
	SpecialToken,
	Whitespace,
	Number,
	ScriptString,
	Comment
}

/**
 * Abstract Lexer Token class
 * All Tokens should be derived from this
 */
export abstract class Token implements IToken {
	contents: string = "";
	start: number = 0;
	end: number = 0;

	/**
	 * Populates this token's values after validation has passed
	 * @param contents The text content of this token
	 * @param start The starting position of this token
	 * @param end The ending position of this token
	 */
	populate(contents: string, start: number, end: number): void {
		this.contents = contents;
		this.start = start;
		this.end = end;
	}

	/**
	 * Gets the contents of this Token
	 * @returns Contents of this token
	 */
	getContents(): string {
		return this.contents;
	}

	/**
	 * Gets the Token's location
	 * @returns The locations of this token in the file
	 */
	getLocation(): [number, number] {
		return [this.start, this.end];
	}

	/**
	 * Gets the unique Token Type for this Token
	 */
	abstract getType(): TokenType;

	/**
	 * Gets the specific subtype of the token, if it applies.
	 */
	abstract getSpecificType(): string;

	/**
	 * Returns the regular expression associated with this token type
	 */
	abstract getRegex(): RegExp;

	toString(): string {
		return `Token: ${this.getType().toString()} at ${this.start}-${this.end}`;
	}
}