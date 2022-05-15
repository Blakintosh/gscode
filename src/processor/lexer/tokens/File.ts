import {Token, TokenType} from "./Token";
import * as vscode from 'vscode';

/* eslint-disable @typescript-eslint/naming-convention */
/**
 * GSC Script Types: one of gsc, csc, gsh (used for any insert)
 */
enum ScriptType {
	Unknown = "unk",
	GSC = "gsc",
	CSC = "csc",
	GSH = "gsh"
}

/**
 * AST Class for the File Root Token
 * Structure:
 * A script file.
 * Examples:
 * n/a
 */
export class File extends Token {
	// Value will attempt to be parsed into its true thing (i.e. an int will be stored as an int) - contents stores the literal
	type: ScriptType;

	constructor() {
		super();
		this.type = ScriptType.Unknown;
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
		return true;
	}
}