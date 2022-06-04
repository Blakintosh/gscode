import { ScriptDiagnostic } from "../../diagnostics/ScriptDiagnostic";
import { TokenReader } from "../../logic/TokenReader";
import * as vscode from "vscode";
import { ScriptSemanticToken } from "../../ScriptSemanticToken";

export abstract class StatementContents {
    diagnostics: ScriptDiagnostic[] = [];
    semantics: ScriptSemanticToken[] = [];
    /**
     * Parses the given statement contents, which may include recursive calls.
     */
    abstract parse(reader: TokenReader): void;

    pushDiagnostic(location: [number, number], message: string, severity: vscode.DiagnosticSeverity | undefined = undefined) {
        this.diagnostics.push(new ScriptDiagnostic(location, message, severity));
    }

    pushSemantic(location: [number, number], tokenType: string, tokenModifiers: string[] | undefined = undefined): void {
        this.semantics.push(new ScriptSemanticToken(location, tokenType, tokenModifiers));
    }
}