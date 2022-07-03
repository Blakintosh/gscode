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
import { ScriptDiagnostic } from "./ScriptDiagnostic";
import { ScriptError } from "./ScriptError";

export class ParserDiagnostics {
    document: vscode.TextDocument;
    diagnostics: ScriptDiagnostic[] = [];
    lineLocations: number[] = [];

    constructor(document: vscode.TextDocument) {
        this.document = document;
        this.resolveLineLocations();
    }

    /**
     * Produces a location array that stores the base character index of every line in the file.
     */
    private resolveLineLocations(): void {
        let text = this.document.getText();

        let locations = this.lineLocations;
        locations[0] = 0;

        let i = 0;
        let basePos = locations[i];
        let index = text.indexOf("\r\n", basePos);
        while(index !== -1) {
            locations[i++ + 1] = index + 1;

            basePos = locations[i];
            index = text.indexOf("\r\n", basePos);
        }
    }

    /**
     * Converts a given absolute character position to a line and character.
     * @param position The absolute position within the file
     * @returns A VSCode-compatible Position with line and character index
     */
    private absolutePositionToLineChar(position: number): vscode.Position {
        let line = 0;

        // Iterates through the known line locations and picks the one that this position belongs to
        for(let i = 0; i < this.lineLocations.length; i++) {
            if(this.lineLocations[i] > position) {
                line = i - 1;
                break;
            }
        }

        // Resolve character by subtracting the base position of this line
        let char = position - this.lineLocations[line];

        return new vscode.Position(line, char);
    }

    /**
     * Pushes a given diagnostic to the diagnostics array, to later be sent to VSCode's API.
     * @param diagnostic The diagnostic to push
     */
    pushDiagnostic(location: [number, number], message: string, source: string, severity?: vscode.DiagnosticSeverity, tags?: vscode.DiagnosticTag[]): void {
		// Push new to array
        this.diagnostics.push(new ScriptDiagnostic(
            location,
            message,
			source,
            severity,
			tags
        ));
    }

	pushFromError(e: unknown): void {
		if(e instanceof ScriptError) {
			this.diagnostics.push(e.errorData);
		} else {
			throw e;
		}
	}
}