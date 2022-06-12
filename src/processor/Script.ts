import { Lexer } from "./lexer/Lexer";
import { Parser } from "./parser/Parser";
import * as vscode from "vscode";
import { EOL } from "os";

export class Script {
	busy: boolean = true;
	private lexer: Lexer;
	private parser: Parser;
    private lineLocations: number[] = [];

	async parse(): Promise<void> {
		this.lexer.tokenize();
		this.parser.parse();

		this.busy = false;
	}

    /**
     * Produces a location array that stores the base character index of every line in the file.
     */
    private resolveLineLocations(document: vscode.TextDocument): void {
        let text = document.getText();

        let locations = this.lineLocations;
        locations[0] = 0;

        let i = 0;
        let basePos = locations[i];
        let index = text.indexOf(EOL, basePos);
        while(index !== -1) {
            locations[i++ + 1] = index + EOL.length;

            basePos = locations[i];
            index = text.indexOf(EOL, basePos);
        }
		locations[i++ + 1] = text.length;

		console.log(locations);
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

	getDiagnostics(): vscode.Diagnostic[] {
		let diagnostics: vscode.Diagnostic[] = [];
		for(let diagnostic of this.parser.diagnostic.diagnostics) {
			// Create a VScode diagnostic and push it to our array
			let vscDiagnostic = new vscode.Diagnostic(new vscode.Range(
				this.absolutePositionToLineChar(diagnostic.location[0]),
				this.absolutePositionToLineChar(diagnostic.location[1])
			), diagnostic.message, diagnostic.severity);
			vscDiagnostic.tags = diagnostic.tags;

			diagnostics.push(vscDiagnostic);
		}

		return diagnostics;
	}

	constructor(document: vscode.TextDocument) {
		this.busy = true;
        this.resolveLineLocations(document);

		this.lexer = new Lexer(document);
		this.parser = new Parser(document, this.lexer);
	}
}