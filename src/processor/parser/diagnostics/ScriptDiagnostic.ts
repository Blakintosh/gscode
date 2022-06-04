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
    readonly severity?: vscode.DiagnosticSeverity;

    constructor(location: [number, number], message: string, severity: vscode.DiagnosticSeverity | undefined) {
        this.location = location;
        this.message = message;
        this.severity = severity;
    }
}