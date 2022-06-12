import { ScriptProcessor } from "./ScriptProcessor";
import * as vscode from "vscode";

export class ScriptDiagnosticProvider {
	static async provideDiagnostics(document: vscode.TextDocument, collection: vscode.DiagnosticCollection): Promise<void> {
		let script = await ScriptProcessor.get(document);
		if(script) {
			let diagnostics = script.getDiagnostics();

			collection.set(document.uri, diagnostics);
		}
	}
}