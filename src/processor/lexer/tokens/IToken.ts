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

import {TokenType} from './Token';

export interface IToken {
	/**
	 * Populates this token's values after validation has passed
	 * @param contents The text content of this token
	 * @param start The starting position of this token
	 * @param end The ending position of this token
	 */
	populate(contents: string, start: number, end: number): void;

	/**
	 * Returns the regular expression associated with this token type
	 */
	getRegex(): RegExp;

	/**
	 * Returns the token's unique type
	 */
	getType(): TokenType;

	/**
	 * Gets the specific subtype of the token, if it applies.
	 */
	getSpecificType(): string;

	/**
	 * Gets the Token's location
	 * @returns The locations of this token in the file
	 */
	getLocation(): [number, number];
}