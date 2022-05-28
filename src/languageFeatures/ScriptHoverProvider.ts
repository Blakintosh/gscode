// The module 'vscode' contains the VS Code extensibility API
// Import the module and reference it with the alias vscode in your code below
import * as vscode from 'vscode';
import { Library } from '../processor/library/Library';

export class ScriptHoverProvider implements vscode.HoverProvider {
	provideHover(document: vscode.TextDocument, position: vscode.Position, token: vscode.CancellationToken): vscode.ProviderResult<vscode.Hover> {
		const range = document.getWordRangeAtPosition(position);
		const word = document.getText(range);

		const libraryMatch = Library.getFunction(document.languageId, word);

		if(libraryMatch !== undefined) {
			return new vscode.Hover(libraryMatch.toDocString());
		}
		return null;
	}
}