import * as vscode from 'vscode';
import { IToken } from '../interfaces/IToken';
import {Token, TokenType} from "./Token";

/* eslint-disable @typescript-eslint/naming-convention */
enum ConditionalType {
	Unknown = "unk",
	If = "if",
	ElseIf = "else if",
	Else = "else"
}

/**
 * AST Class for a Conditional Token
 * Structure:
 * {ConditionalType} [( {Expression} )]
 * Examples:
 * if(true), else if(a && !b), else
 */
export class Conditional extends Token {
	type: ConditionalType;

	constructor() {
		super();
		this.type = ConditionalType.Unknown;
	}

	/**
	 * Validates whether the next Token in the file matches this type.
	 * @param position Current base position in the file.
	 * @returns true if matches, false otherwise
	 */
	tokenMatches(inputText: String, position: number): boolean {
		throw new Error('Method not implemented.');
	}

	getType(): TokenType {
		return TokenType.Conditional;
	}

	pushSemanticTokens(builder: vscode.SemanticTokensBuilder): void {
		// Not implemented
	}

	isBranch(): boolean {
		return true;
	}
}