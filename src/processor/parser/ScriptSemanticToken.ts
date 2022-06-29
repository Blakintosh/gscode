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

/**
 * Class that stores a semantic token to be pushed to VSCode's API later
 * in processing.
 */
export class ScriptSemanticToken {
    readonly location: [number, number];
    readonly tokenType: string;
    readonly tokenModifiers?: string[];

    constructor(location: [number, number], tokenType: string, tokenModifiers: string[] | undefined = undefined) {
        this.location = location;
        this.tokenType = tokenType;
        this.tokenModifiers = tokenModifiers;
    }
}