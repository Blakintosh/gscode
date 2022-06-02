import * as vscode from 'vscode';
import {TokenType} from './Token';

export interface IToken {
	/**
	 * Populates this token's values after validation has passed
	 * @param contents The text content of this token
	 * @param start The starting position of this token
	 * @param end The ending position of this token
	 */
	populate(contents: string, start: number, end: number): void;

	/**
	 * Returns the regular expression associated with this token type
	 */
	getRegex(): RegExp;

	/**
	 * Returns the token's unique type
	 */
	getType(): TokenType;

	/**
	 * Gets the specific subtype of the token, if it applies.
	 */
	getSpecificType(): string;

	/**
	 * Gets the Token's location
	 * @returns The locations of this token in the file
	 */
	getLocation(): [number, number];
}