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

import * as vscode from "vscode";

/**
 * The ScriptDiagnostic class is similar to the vscode Diagnostic except that it
 * stores position as the absolute index in the text file, not by line and char.
 * This allows location determination to be handled later when diagnostics are
 * pushed to vscode, via the ParserDiagnostic class.
 */
export class ScriptDiagnostic {
    readonly location: [number, number];
    readonly message: string;
	readonly source: string;
    readonly severity?: vscode.DiagnosticSeverity;
	readonly tags?: vscode.DiagnosticTag[];

    constructor(location: [number, number], message: string, source: string, severity?: vscode.DiagnosticSeverity, tags?: vscode.DiagnosticTag[]) {
        this.location = location;
        this.message = message;
		this.source = source;
        this.severity = severity;
		this.tags = tags;
    }
}