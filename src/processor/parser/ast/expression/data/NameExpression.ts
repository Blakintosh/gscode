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
import { StatementContents } from "../StatementContents";

export class NameExpression extends StatementContents {
	value?: string;
	location?: [number, number];

    /**
     * Parses the given statement contents, which may include recursive calls.
     * For a name there are no nested calls.
     */
    parse(reader: ScriptReader): void {
        let token = reader.readToken();
		reader.index++;

        // Check token could be a path
        if(token.getType() !== TokenType.Name) {
            // This token can't be a name, abort parsing
			throw new ScriptError(token.getLocation(), "Expected name.", GSCProcessNames.Parser);
        }

		this.value = (<Token> token).contents;
		
		// Store the location as it validates
        this.location = token.getLocation();

        // Done
    }
}