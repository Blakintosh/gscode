import {Token, TokenType} from "./Token";
import * as vscode from 'vscode';

/* eslint-disable @typescript-eslint/naming-convention */
/**
 * GSC references
 */
enum ReferenceType {
	Variable,
	FunctionPointer,
	Struct,
	Object,
	BranchArgument,
	Unknown
}

/**
 * AST Class for an Reference Token
 * Structure:
 * {Reference}
 * Examples:
 * foo, bar, &foo
 */
export class Reference extends Token {
	type: ReferenceType;

	constructor() {
		super();
		this.type = ReferenceType.Unknown;
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