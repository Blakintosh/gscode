/* eslint-disable @typescript-eslint/naming-convention */
import * as vscode from 'vscode';
import { IToken } from '../interfaces/IToken';

/**
 * Enum for all the possible token types
 * All derivatives of Token & IToken should uniquely match to one of these
 */
export enum TokenType {
	Unknown,
	File,
	Operator,
	Data,
	Expression,
	Conditional,
	Switch,
	Case,
	Loop,
	ClassDeclaration,
	FunctionDeclaration,
	VariableDeclaration
}

/**
 * Abstract AST class for a Token
 * All Tokens should be derived from this
 */
export abstract class Token implements IToken {
	contents: string;
	start: number;
	end: number;

	/**
	 * Constructor that populates basic values
	 */
	constructor()
	{
		this.contents = "";
		this.start = 0;
		this.end = 0;
	}

	/**
	 * Gets the contents of this Token
	 * @returns Contents of this token
	 */
	getContents(): string {
		return this.contents;
	}

	/**
	 * Gets the Token's location
	 * @returns The locations of this token in the file
	 */
	getLocation(): [number, number] {
		return [this.start, this.end];
	}

	/**
	 * Pushes this token, and its children tokens, onto the Semantic Tokens Builder
	 * @param builder Reference to the builder
	 */
	abstract pushSemanticTokens(builder: vscode.SemanticTokensBuilder): void;

	/**
	 * Gets the unique Token Type for this Token
	 */
	abstract getType(): TokenType;

	/**
	 * Returns whether the next Token in the file matches this type
	 * @param position Current file position
	 */
	abstract tokenMatches(inputText: String, position: number): boolean;

	/**
	 * Returns whether this token leads to a branch
	 */
	abstract isBranch(): boolean;
}