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
import { ScriptValue } from "./ScriptValue";

// Valid precache types in GSC/CSC. This may not be the full collection, especially not for CSC
// (If a reverse engineer wants to help here, that'd be nice!)
export const PrecacheTypes = {
	"gsc": {
		String: "string",
		TriggerString: "triggerstring",
		Objective: "objective",
		FX: "fx",
		Menu: "menu",
		Material: "material",
		Model: "model",
		StatusIcon: "statusicon",
		EventString: "eventstring",
		LocationSelector: "locatorselector",
		XModel: "xmodel",
		LuiMenu: "lui_menu",
		LuiMenuData: "lui_menu_data"
	},
	"csc": {
		ClientFX: "client_fx",
		ClientTagFXSet: "client_tagfxset",
	}
};

export class ScriptPrecache {
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