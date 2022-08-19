/**
	GSCode Language Extension for Visual Studio Code
    Copyright (C) 2022 Blakintosh

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

// The module 'vscode' contains the VS Code extensibility API
// Import the module and reference it with the alias vscode in your code below
import * as vscode from 'vscode';

import {
	LanguageClient,
	LanguageClientOptions,
	ServerOptions,
	TransportKind
} from 'vscode-languageclient/node';

// this method is called when your extension is activated
// your extension is activated the very first time the command is executed
export function activate(context: vscode.ExtensionContext) {
	
	// Use the console to output diagnostic information (console.log) and errors (console.error)
	// This line of code will only be executed once when your extension is activated
	console.log('Congratulations, your extension "gsc" is now active!');

	// The command has been defined in the package.json file
	// Now provide the implementation of the command with registerCommand
	// The commandId parameter must match the command field in package.json
	let disposable = vscode.commands.registerCommand('gsc.helloWorld', () => {
		// The code you place here will be executed every time your command is executed
		// Display a message box to the user
		let env = process.env["TA_TOOLS_PATH"];
		if(env !== undefined) {
			vscode.window.showInformationMessage("test "+env);
			
		}
	});

	/*let serverOptions: ServerOptions = {
		//run: { command: "dotnet", }
	};*/

	// Register the completion providers
	//vscode.languages.registerCompletionItemProvider("gsc", new ScriptCompletionItemProvider());
	//vscode.languages.registerCompletionItemProvider("csc", new ScriptCompletionItemProvider());

	// Register the hover providers
	//vscode.languages.registerHoverProvider('gsc', new ScriptHoverProvider());
	//vscode.languages.registerHoverProvider('csc', new ScriptHoverProvider());
	
	// Diagnostic provider for GSCode
	//let diagnosticCollection: vscode.DiagnosticCollection = vscode.languages.createDiagnosticCollection('gsc');
	

	//semantics.provide();

	context.subscriptions.push(disposable);

	//context.subscriptions.push(diagnosticCollection);

	// Refresh the loaded script every time it's edited or active editor changes
	context.subscriptions.push(vscode.workspace.onDidChangeTextDocument(event => {
		if (event) {
			//ScriptProcessor.refresh(event.document);
			//ScriptDiagnosticProvider.provideDiagnostics(event.document, diagnosticCollection);
		}
	}));
	context.subscriptions.push(vscode.window.onDidChangeActiveTextEditor(editor => {
		if (editor) {
			//ScriptProcessor.refresh(editor.document);
			//ScriptDiagnosticProvider.provideDiagnostics(editor.document, diagnosticCollection);
		}
	}));
}

// this method is called when your extension is deactivated
export function deactivate() {}
