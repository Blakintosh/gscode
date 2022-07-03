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

import { IToken } from "../../lexer/tokens/IToken";
import { TokenType } from "../../lexer/tokens/Token";

export class TokenRule {
    readonly type: TokenType;
    // Here the "string" should actually be a string enum
    readonly specificType?: string;
	readonly optional: boolean;

    constructor(type: TokenType, specificType: string | undefined = undefined, optional: boolean = false) {
        this.type = type;
        this.specificType = specificType;
		this.optional = optional;
    }

    matches(token: IToken): boolean {
        if(this.type !== token.getType()) {
            return false;
        } else if(this.specificType !== undefined && this.specificType !== token.getSpecificType()) {
            return false;
        }
        return true;
    }
}