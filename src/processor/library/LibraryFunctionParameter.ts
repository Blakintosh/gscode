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
 * Class that stores the information associated with a built-in function parameter.
 */
export class LibraryFunctionParameter {
	readonly name: string;
	readonly description: string;
	readonly mandatory: boolean;

	constructor(name: string, description: string, mandatory=true) {
		this.name = name;
		this.description = description;
		this.mandatory = mandatory;
	}
}