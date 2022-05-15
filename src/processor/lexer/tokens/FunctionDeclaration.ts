import * as vscode from 'vscode';
import {Token, TokenType} from "./Token";

/**
 * AST Class for a Function Declaration Token
 * Structure:
 * function {Name} ([Argument]) {
 * Examples:
 * function cheese(board) {, function idk(count) {, function foo(bar) return;
 */
export class FunctionDeclaration extends Token {

	constructor() {
		super();
	}

	pushSemanticTokens(builder: vscode.SemanticTokensBuilder): void {
		// Not implemented
	}

	getType(): TokenType {
		throw new Error('Method not implemented.');
	}

	/**
	 * Validates whether the next Token in the file matches this type.
	 * @param position Current base position in the file.
	 * @returns true if matches, false otherwise
	 */
	tokenMatches(inputText: String, position: number): boolean {
		let regex = /(function)\s(\w+)(\s*)(\([^\)]*\))/;
		throw new Error('Method not implemented.');
	}

	isBranch(): boolean {
		return true;
	}
}