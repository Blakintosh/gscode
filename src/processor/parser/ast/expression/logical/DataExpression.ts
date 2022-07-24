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

import { Token, TokenType } from "../../../../lexer/tokens/Token";
import { GSCProcessNames } from "../../../../util/GSCUtil";
import { ScriptError } from "../../../diagnostics/ScriptError";
import { ScriptReader } from "../../../logic/ScriptReader";
import { LiteralDataExpression } from "../data/LiteralDataExpression";
import { StatementContents } from "../StatementContents";
import { VariableExpression } from "../types/VariableExpression";

export class DataExpression extends StatementContents {
	reference?: VariableExpression;
	data?: LiteralDataExpression;

	/**
	 * Parses the given statement contents, which may include recursive calls.
	 * Data either recurses into reference or literal data depending on what it is reading.
	 */
	parse(reader: ScriptReader): void {
		const baseIndex = reader.index;
		
		// Try parse as a reference initially
		try {
			// Parse the reference
			this.reference = new VariableExpression();
			this.reference.parse(reader);
		} catch(e) {
			reader.index = baseIndex;
			this.reference = undefined;
		}

		// Otherwise try parse as data
		if(!this.reference) {
			try {
				// Parse the data
				this.data = new LiteralDataExpression();
				this.data.parse(reader);
			} catch(e) {
				reader.index = baseIndex;
				this.data = undefined;
			}
		}

		if(this.data === undefined && this.reference === undefined) {
			throw new ScriptError(reader.readToken().getLocation(), "Expected data or reference.", GSCProcessNames.Parser);
		}

		// Done
	}
}