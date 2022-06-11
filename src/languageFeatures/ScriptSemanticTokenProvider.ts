// The module 'vscode' contains the VS Code extensibility API
// Import the module and reference it with the alias vscode in your code below
import * as vscode from 'vscode';
import { Library } from '../processor/library/Library';

export class ScriptSemanticTokenProvider implements vscode.DocumentSemanticTokensProvider {
	onDidChangeSemanticTokens?: vscode.Event<void> | undefined;
	
	provideDocumentSemanticTokens(document: vscode.TextDocument, token: vscode.CancellationToken): vscode.ProviderResult<vscode.SemanticTokens> {
		throw new Error('Method not implemented.');
	}
}