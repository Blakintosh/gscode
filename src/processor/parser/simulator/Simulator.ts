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

import { Parser } from "../Parser";

/**
 * The Simulator class is used to simulate the execution of a script.
 * After the script has been parsed, the Simulator will analyse the AST.
 * It performs validation on references, validates file paths, etc.
 * (In a later version) It will also attempt to execute expressions.
 */
export class Simulator {
	parser: Parser;

	constructor(parser: Parser) {
		this.parser = parser;
	}
}