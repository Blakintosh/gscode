// The module 'vscode' contains the VS Code extensibility API
// Import the module and reference it with the alias vscode in your code below
import * as vscode from 'vscode';
import { Library } from '../processor/library/Library';

export class ScriptCompletionItemProvider implements vscode.CompletionItemProvider {
    public provideCompletionItems(document: vscode.TextDocument, position: vscode.Position, token: vscode.CancellationToken) {
		// Determine language and return appropriate completion items
		switch(document.languageId) {
			case "gsc":
				return Library.gscFunctionArray;
			case "csc":
				return Library.cscFunctionArray;
			case "gsh":
				return Library.gscFunctionArray.concat(Library.cscFunctionArray);
		}
    }
}