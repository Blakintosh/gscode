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
	 * @param position The absolute position in the file
	 * @returns A token object for this location
	 */
	private getNextToken(text: string, position: number): IToken {
		// Ordered by priority of token types
		let potentialTokens = [new Whitespace(), new Comment(), new SpecialToken(), new Operator(), new Punctuation(), new Keyword(), new ScriptString(), new Number(), new Name()];
			
		let textLoc = text.substring(position);

		for (let token of potentialTokens) {
			let regex = token.getRegex();

			let match = textLoc.match(regex);

			if(match !== null && match.index === 0) {
				let contents = match[0];
				let start = position;
				let end = position + contents.length;

				token.populate(contents, start, end);

				return token;
			}
		}
		throw new Error("No token could be found at position " + position);
	}

	tokenize(): void {
		let text = this.file.getText();
		let position = 0;

		let start = performance.now();

		while (position < text.length) {
			let token = this.getNextToken(text, position);
			if(token.getType() !== TokenType.Whitespace) {
				this._tokens.push(token);
			}
			position = token.getLocation()[1];
		}

		let end = performance.now();
		console.log(`Successfully tokenized ${this.file.fileName} in ${end - start}ms`);
	}
}