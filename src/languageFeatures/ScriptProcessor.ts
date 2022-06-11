import { Script } from "../processor/Script";
import * as vscode from "vscode";
import { waitUntil } from 'async-wait-until';

export class ScriptProcessor {
	static scripts: Map<string, Script> = new Map<string, Script>();

	static async addScript(document: vscode.TextDocument): Promise<void> {
		let script = new Script(document);

		let canonicalFile = document.uri.toString();
		this.scripts.set(canonicalFile, script);

		await script.parse();
	}

	static async get(document: vscode.TextDocument): Promise<Script | undefined> {
		let canonicalFile = document.uri.toString();
		if(!this.scripts.has(canonicalFile)) {
			await this.addScript(document);
			let script = this.scripts.get(canonicalFile);
			if(script) {
				return script;
			}
		} else {
			let script = this.scripts.get(canonicalFile);
			if(script) {
				if(script.busy) {
					await waitUntil(() => (script && !script.busy), {timeout: 5000});
				}
				return script;
			}
		}
	}

	static clear(document: vscode.TextDocument): void {
		let canonicalFile = document.uri.toString();
		if(!this.scripts.has(canonicalFile)) {
			this.scripts.delete(canonicalFile);
		}
	}

	static async refresh(document: vscode.TextDocument): Promise<void> {
		if(document.languageId !== "gsc" && document.languageId !== "csc" && document.languageId !== "gsh") {
			return;
		}
		
		console.log(`Parsing ${document.fileName}...`);

		try {
			ScriptProcessor.clear(document);
			await ScriptProcessor.addScript(document);
		} catch(e) {
			console.error(`Could not parse ${document.fileName}: ${e}`);
		}
	}
}