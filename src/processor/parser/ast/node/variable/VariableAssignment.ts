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
import { ScriptReader } from "../../../logic/ScriptReader";
import { IASTNode } from "../IASTNode";

/**
 * Variable Assignment Syntax
 * First token can be a Name or a Function call, possibly indexed with [String/Integer]
 * after any sequence of Name, possibly indexed with [String/Integer]
 * Each split by a .
 */

export class VariableAssignment implements IASTNode {
	getChildren(): IASTNode[] {
		throw new Error("Method not implemented.");
	}
	pushDiagnostic(location: [number, number], message: string, severity: DiagnosticSeverity | undefined): void {
		throw new Error("Method not implemented.");
	}
	pushSemantic(location: [number, number], tokenType: string, tokenModifiers: string[] | undefined): void {
		throw new Error("Method not implemented.");
	}
	matches(reader: ScriptReader): boolean {
		throw new Error("Method not implemented.");
	}
	parse(reader: ScriptReader, allowedChildren: IASTNode[] | undefined): void {
		throw new Error("Method not implemented.");
	}
	
}