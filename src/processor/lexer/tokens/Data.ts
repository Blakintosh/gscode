import {Token, TokenType} from "./Token";
import * as vscode from 'vscode';

/* eslint-disable @typescript-eslint/naming-convention */
/**
 * ~GSC Data Types: int, float, string, boolean, undefined
 * or a Reference to a var, function, etc.
 */
enum DataType {
	Int = "int",
	Float = "float",
	Boolean = "bool",
	String = "string",
	Undefined = "undefined",
	Reference = "reference",
	Unknown = "unknown"
}

/**
 * AST Class for a Factor Token
 * Structure:
 * {Factor}
 * Examples:
 * &scene::init, 2, false, a, "yes"
 */
export class Data extends Token {
	// Value will attempt to be parsed into its true thing (i.e. an int will be stored as an int) - contents stores the literal
	value: number | undefined | String | null;
	type: DataType;

	constructor() {
		super();
		this.value = null;
		this.type = DataType.Unknown;
	}

	pushSemanticTokens(builder: vscode.SemanticTokensBuilder): void {
		// Not implemented
	}
	
	/**
	 * Validates whether the next Token in the file matches this type.
	 * @param position Current base position in the file.
	 * @param prefix Prefix that may be applied to expand RegEx search in the case of a syntax error.
	 * @returns true if matches, false otherwise
	 */
	static tokenMatches(position: number, prefix: RegExp): boolean {
		throw new Error('Method not implemented.');
	}
	
	getType(): TokenType {
		return TokenType.Data;
	}

	tokenMatches(inputText: String, position: number): boolean {
		throw new Error("Method not implemented.");
	}

	isBranch(): boolean {
		return false;
	}
}