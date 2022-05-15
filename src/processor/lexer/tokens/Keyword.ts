import {Token, TokenType} from "./Token";
import * as vscode from 'vscode';

/* eslint-disable @typescript-eslint/naming-convention */
/**
 * Keywords and modifiers that GSC supports. Ordered by char count as first match will be used
 */
enum KeywordType {
	Unknown = "unk",
	Function = "function",
	Autoexec = "autoexec",
	Private = "private",
	In = "in",
}

/**
 * AST Class for an Keyword Token
 * Structure:
 * {Keyword}
 * Examples (see above)
 */
export class Keyword extends Token {
	type: KeywordType;

	constructor() {
		super();
		this.type = KeywordType.Unknown;
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