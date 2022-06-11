import * as vscode from "vscode";
import { ScriptDiagnostic } from "./ScriptDiagnostic";

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
    pushDiagnostic(location: [number, number], message: string, severity?: vscode.DiagnosticSeverity, tags?: vscode.DiagnosticTag[]): void {
        // Locate the diagnostic's position in terms of line and character index, then push to a range
        /*let startPos = this.absolutePositionToLineChar(location[0]);
        let endPos = this.absolutePositionToLineChar(location[1]);

        let range = new vscode.Range(startPos, endPos);*/

        // Create the VSCode diagnostic and push it to the diagnostic array
        this.diagnostics.push(new ScriptDiagnostic(
            location,
            message,
            severity,
			tags
        ));
    }
}