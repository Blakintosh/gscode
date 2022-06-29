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
import { ScriptDependency } from "../data/ScriptDependency";
import { ScriptSemanticToken } from "../ScriptSemanticToken";
import { ParserDiagnostics } from "../diagnostics/ParserDiagnostics";
import * as vscode from "vscode";
import path = require("path");
import { ScriptScope } from "../data/ScriptScope";
import { ScriptVariable } from "../data/ScriptVariable";
import { TokenType } from "../../lexer/tokens/Token";

export class ScriptReader {
    index: number = 0;
    readonly tokens: IToken[];
	readonly diagnostic: ParserDiagnostics;
	dependencies: ScriptDependency[];
	//precaches: 
	semanticTokens: ScriptSemanticToken[];
	readonly format: string;
	currentNamespace: string;
	scopeStack: Array<ScriptScope> = new Array<ScriptScope>();

    constructor(document: vscode.TextDocument, tokens: IToken[], dependencies: ScriptDependency[], diagnostic: ParserDiagnostics, semanticTokens: ScriptSemanticToken[]) {
        this.tokens = tokens;
		this.format = document.languageId;
		const fileName = path.basename(document.fileName);
		this.currentNamespace = fileName.substring(fileName.lastIndexOf("."));
		this.dependencies = dependencies;
		this.diagnostic = diagnostic;
		this.semanticTokens = semanticTokens;
    }

    readToken(offset: number = 0): IToken {
		if(this.atEof()) {
			throw new Error("Attempt to read beyond the end of the script file.");
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

	atEof(): boolean {
		return this.index >= this.tokens.length;
	}

	getVar(name: string): ScriptVariable | undefined {
		for(const scope of this.scopeStack) {
			let variable = scope.vars.find(v => v.name === name);
			if(variable) {
				return variable;
			}
		}
		return undefined;
	}

	pushVar(variable: ScriptVariable): void {
		this.scopeStack[this.scopeStack.length - 1].vars.push(variable);
	}

	increaseScope(): void {
		this.scopeStack.push(new ScriptScope());
	}

	decreaseScope(): void {
		this.scopeStack.pop();
	}
}