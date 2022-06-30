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

import { DiagnosticSeverity } from "vscode";
import { TokenType } from "../../../../../lexer/tokens/Token";
import { PunctuationTypes } from "../../../../../lexer/tokens/types/Punctuation";
import { ScriptReader } from "../../../../logic/ScriptReader";
import { TokenRule } from "../../../../logic/TokenRule";
import { IASTNode } from "../../IASTNode";

/**
 * This Macro Function Call class will only be used in the root branch of the AST.
 * The Function Call class will handle Macro & Script calls in nested branches.
 * 
 * Rule: {Name}(
 */
export class MacroFunctionCall implements IASTNode {
	getChildren(): IASTNode[] {
		throw new Error("Method not implemented.");
	}

	matches(reader: ScriptReader): boolean {
		const openParen = new TokenRule(TokenType.Punctuation, PunctuationTypes.OpenParen);
		
		return (
			reader.readToken().getType() === TokenType.Name &&
			openParen.matches(reader.readAhead())
		);
	}

	parse(reader: ScriptReader, allowedChildren: IASTNode[] | undefined): void {
		throw new Error("Method not implemented.");
	}

}