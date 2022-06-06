/**
	GSCode Language Extension for Visual Studio Code
    Copyright (C) 2022 Blakintosh

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

import { IToken } from "./tokens/IToken";
import * as vscode from "vscode";

// Token types
import { Keyword } from "./tokens/types/Keyword";
import { Name } from "./tokens/types/Name";
import { Operator } from "./tokens/types/Operator";
import { Punctuation } from "./tokens/types/Punctuation";
import { SpecialToken } from "./tokens/types/SpecialToken";
import { Whitespace } from "./tokens/types/Whitespace";
import { ScriptString } from "./tokens/types/ScriptString";
import { Number } from "./tokens/types/Number";
import { TokenType } from "./tokens/Token";
import { Comment } from "./tokens/types/Comment";
import { performance } from "perf_hooks";
import { Unknown } from "./tokens/types/Unknown";

/**
 * Implementation of a GSC Lexer, based on Treyarch's documentation
 */
export class Lexer {
	private _tokens: IToken[] = [];
	get tokens(): IToken[] {
		return this._tokens;
	}

	readonly file: vscode.TextDocument;

	constructor(file: vscode.TextDocument) {
		this.file = file;
	}

	/**
	 * Gets the next token in this substring of the file
	 * @param text The text to tokenize
	 * @param offset The current position we have substringed to so far
	 * @param position The absolute position in the file
	 * @returns A token object for this location
	 */
	private getNextToken(text: string, currOffset: number, position: number): IToken {
		// Ordered by priority of token types
		let potentialTokens = [new Whitespace(), new Comment(), new SpecialToken(), new Operator(), new Punctuation(), new Keyword(), new ScriptString(), new Number(), new Name()];
			
		text = text.substring(position - currOffset);
		currOffset = position;

		for (let token of potentialTokens) {
			let regex = token.getRegex();

			let match = text.match(regex);

			if(match !== null && match.index === 0) {
				let contents = match[0];
				let start = position;
				let end = position + contents.length;

				token.populate(contents, start, end);

				return token;
			}
		}
		// If no token matches, advance by one character and set current position to an Unknown token
		let token = new Unknown();
		token.populate(text.substring(0, 1), position, position + 1);

		return token;
	}

	/**
	 * Tokenizes the entire file.
	 */
	tokenize(): void {
		let text = this.file.getText();
		let position = 0;
		let currOffset = 0;

		let start = performance.now();

		while (position < text.length) {
			let token = this.getNextToken(text, currOffset, position);
			if(token.getType() !== TokenType.Whitespace) {
				this._tokens.push(token);
			}
			position = token.getLocation()[1];
		}

		let end = performance.now();
		console.log(`Successfully tokenized ${this.file.fileName} in ${end - start}ms`);
	}
}