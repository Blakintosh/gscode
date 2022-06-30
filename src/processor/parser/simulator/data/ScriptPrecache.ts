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

// Valid precache types in GSC/CSC. This may not be the full collection, especially not for CSC
// (If a reverse engineer wants to help here, that'd be nice!)
// TODO: Could be worth changing this into a class
export const PrecacheTypes = [
	{
		Language: "gsc",
		Types: [
			"string",
			"triggerstring",
			"objective",
			"fx",
			"menu",
			"material",
			"model",
			"statusicon",
			"eventstring",
			"locationselector",
			"xmodel",
			"lui_menu",
			"lui_menu_data"
		]
	},
	{
		Language: "csc",
		Types: [
			"client_fx",
			"client_tagfxset"
		]
	}
];

export class ScriptPrecache {
	type?: string;
	value?: string;

	/**
	 * Populates this precache with the given data, failing if the data is invalid.
	 * @param languageId The language ID of the script
	 * @param type The requested precache type
	 * @param value The asset value for this precache
	 * @returns true if valid, false otherwise
	 */
	populate(languageId: string, type: string, value: string): boolean {
		const strippedType = type.replace(/['"]+/g, '');

		// Find a matching type in the language array for the given language
		for(const legend of Object.values(PrecacheTypes)) {
			if(legend.Language === languageId) {
				if(legend.Types.find(x => x === strippedType) !== undefined) {
					this.type = strippedType;
					this.value = value;
					return true;
				}
			}
		}
		return false;
	}

	/**
	 * Returns whether this precache is valid.
	 * @returns true if valid, false otherwise
	 */
	isValid(): boolean {
		return (this.type !== undefined && this.value !== undefined);
	}
}