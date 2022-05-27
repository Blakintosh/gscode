// The module 'vscode' contains the VS Code extensibility API
// Import the module and reference it with the alias vscode in your code below
import * as vscode from 'vscode';
import { Library } from '../processor/library/Library';
import {LibraryCompletionItemLabel} from "../processor/library/LibraryCompletionItemLabel";

export class LibraryCompletionItemProvider implements vscode.CompletionItemProvider {
    public provideCompletionItems(document: vscode.TextDocument, position: vscode.Position, token: vscode.CancellationToken) {
		// get all text until the `position` and check if it reads `"launches.`

		/*const linePrefix = document.lineAt(position).text.substring(0, position.character);
		if (!linePrefix.endsWith('"launches.')) {
			return undefined;
		}*/

		let libraryItem = (text: string | vscode.CompletionItemLabel) => {
			let item = new vscode.CompletionItem(text, vscode.CompletionItemKind.Function);
			item.range = new vscode.Range(position, position);
			item.insertText = "<void> IPrintLnBold(<message>);";
			item.detail = "Example";
			item.documentation = "Function description";
			return item;
		};
		return Library.gscFunctions;
    }
}