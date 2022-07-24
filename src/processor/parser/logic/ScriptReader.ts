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

import { IToken } from "../../lexer/tokens/IToken";
import { ScriptSemanticToken } from "../ScriptSemanticToken";
import { ParserDiagnostics } from "../diagnostics/ParserDiagnostics";
import * as vscode from "vscode";
import path = require("path");
import { TokenType } from "../../lexer/tokens/Token";
import { ScriptEofError } from "../diagnostics/ScriptEofError";

export class ScriptReader {
    index: number = 0;
    readonly tokens: IToken[];
	readonly diagnostic: ParserDiagnostics;
	semanticTokens: ScriptSemanticToken[];
	readonly format: string;
	currentNamespace: string;

    constructor(document: vscode.TextDocument, tokens: IToken[], diagnostic: ParserDiagnostics, semanticTokens: ScriptSemanticToken[]) {
        this.tokens = tokens;
		this.format = document.languageId;
		const fileName = path.basename(document.fileName);
		this.currentNamespace = fileName.substring(fileName.lastIndexOf("."));
		this.diagnostic = diagnostic;
		this.semanticTokens = semanticTokens;
    }

    readToken(offset: number = 0): IToken {
		if(this.wouldBeAtEof(offset)) {
			if(!this.atEof()) {
				console.error(this.readToken());
			}
			let start = 0;
			let end = 0;
			if(offset > 0 && !this.atEof()) {
				while(!this.wouldBeAtEof(end)) {
					end++;
				}
			} else {
				while(this.wouldBeAtEof(start)) {
					start--;
					end--;
				}
			}

			if(this.tokens[this.index + start] && this.tokens[this.index + end]) {
				const startTokenLoc = this.tokens[this.index + start].getLocation();
				const endTokenLoc = this.tokens[this.index + end].getLocation();
				throw new ScriptEofError("Attempt to read beyond the end of the script file.", [startTokenLoc[0], endTokenLoc[1]]);
			} else {
				throw new ScriptEofError("Attempt to read beyond the end of the script file.", [0, 1]);
			}
		}

		if(this.tokens[this.index + offset].getType() === TokenType.Comment) {
			this.index++;
			if(!this.atEof()) {
				return this.readToken();
			}
		}

        return this.tokens[this.index + offset];
    }

	readAhead(): IToken {
		return this.readToken(1);
	}

	readBehind(): IToken {
		return this.readToken(-1);
	}

	getLastTokenLocation(): [number, number] {
		return this.readBehind().getLocation();
	}

	atEof(): boolean {
		let i = this.index;
		while(this.tokens[i] && this.tokens[i].getType() === TokenType.Comment) {
			i++;
		}
		return i >= this.tokens.length;
	}

	wouldBeAtEof(offset: number): boolean {
		return this.index + offset >= this.tokens.length;
	}
}