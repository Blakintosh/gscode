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
import { TokenType } from "../../../lexer/tokens/Token";
import { ScriptReader } from "../../logic/ScriptReader";
import { IASTNode } from "./IASTNode";

export class CommentNode implements IASTNode {
    pushDiagnostic(location: [number, number], message: string, severity: DiagnosticSeverity | undefined): void {
        throw new Error("Method not implemented.");
    }
    pushSemantic(location: [number, number], tokenType: string, tokenModifiers: string[] | undefined): void {
        throw new Error("Method not implemented.");
    }
    matches(reader: ScriptReader): boolean {
        return (reader.readToken().getType() === TokenType.Comment);
    }
    /**
     * Gets the children of a comment, which never exist.
     * @returns An empty array.
     */
    getChildren(): IASTNode[] {
        return [];
    }

	/**
     * Parses the given comment, which does nothing.
     */
	parse(reader: ScriptReader): void {
		return;
	};
}