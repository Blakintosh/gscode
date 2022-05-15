import * as vscode from 'vscode';
import {TokenType} from '../tokens/Token';

export interface IToken {
	/**
	 * Recursively pushes its and the children tokens to the VSCode semantic builder
	 * @param builder The semantic tokens builder to provide to
	 */
	pushSemanticTokens(builder: vscode.SemanticTokensBuilder): void;

	/**
	 * Validates whether the next Token in the file matches this type.
	 * @param position Current base position in the file.
	 * @returns true if matches, false otherwise
	 */
	tokenMatches(inputText: String, position: number): boolean;

	/**
	 * Returns the token's unique type
	 */
	getType(): TokenType;

	/**
	 * Returns whether this token leads to a branch
	 */
	isBranch(): boolean;
}