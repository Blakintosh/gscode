import {Token, TokenType} from "./Token";
import {Data} from "./Data";
import {Operator} from "./Operator";
import * as vscode from 'vscode';

/**
 * AST Class for an Expression Token
 * Structure:
 * {Left} {Operator} {Right}
 * Examples:
 * a * b, 20 - 16, 5 >> b, true && false, x ^ y
 */
export class Expression extends Token {
	// Expression stored when there are nested expressions, factor stored when there is a value or a reference
	left: Expression | Data | null;
	right: Expression | Data | null;
	// Operator in use
	operator: Operator | null;

	constructor() {
		super();
		this.left = null;
		this.right = null;
		this.operator = null;
	}

	pushSemanticTokens(builder: vscode.SemanticTokensBuilder): void {
		// Not implemented
	}
	
	getType(): TokenType {
		throw new Error("Method not implemented.");
	}
	
	/**
	 * Validates whether the next Token in the file matches this type.
	 * @param position Current base position in the file.
	 * @param prefix Prefix that may be applied to expand RegEx search in the case of a syntax error.
	 * @returns true if matches, false otherwise
	 */
	tokenMatches(inputText: String, position: number): boolean {
		throw new Error("Method not implemented.");
	}

	isBranch(): boolean {
		return false;
	}
}