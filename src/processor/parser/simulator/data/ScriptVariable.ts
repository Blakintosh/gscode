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

import { ScriptValue } from "./ScriptValue";

export class ScriptVariable {
	// The name of this variable
	name: string;
	// Whether it is a constant
	constant: boolean;
	// Whether this variable comes from a branch root, e.g. a function argument
	fromBranchRoot: boolean;
	// TODO at later stage: add type-based parsing
	type: any;
	// TODO
	//value: ScriptValue;

	constructor(name: string, constant: boolean = false, fromBranchRoot: boolean = false) {
		this.name = name;
		this.constant = constant;
		this.fromBranchRoot = fromBranchRoot;
	}
}