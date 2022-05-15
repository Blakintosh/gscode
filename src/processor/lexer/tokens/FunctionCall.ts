import * as vscode from 'vscode';
import {Token, TokenType} from "./Token";

/**
 * AST Class for a Function Call Token
 * Structure:
 * [namespace::]{call}([args]);
 * Examples:
 * scene::init(), foo(), cheese(board)
 */
export class FunctionCall extends Token {

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
		let regex = /(?:(\w+)(::))*(\w+)\s*\(([^\)]*)\)/;

		let test = "idk::foo(); cheese();";

		console.log(test.match(regex));
		console.log((test.match(regex) !== null));

		return (test.match(regex) !== null);
	}

	isBranch(): boolean {
		return true;
	}
}