import * as vscode from "vscode";

export class LibraryCompletionItemLabel implements vscode.CompletionItemLabel {
	label: string;
	detail?: string | undefined;
	description?: string | undefined;

	constructor(label: string, detail?: string, description?: string) {
		this.label = label;
		this.detail = detail;
		this.description = description;
	}
}