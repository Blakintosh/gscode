import { IToken } from "../../lexer/tokens/IToken";
import { ScriptDependency } from "../data/ScriptDependency";
import { ScriptSemanticToken } from "../ScriptSemanticToken";
import { ParserDiagnostics } from "../diagnostics/ParserDiagnostics";
import * as vscode from "vscode";
import path = require("path");
import { ScriptScope } from "../data/ScriptScope";
import { ScriptVariable } from "../data/ScriptVariable";

export class ScriptReader {
    index: number = 0;
    readonly tokens: IToken[];
	readonly diagnostic: ParserDiagnostics;
	dependencies: ScriptDependency[];
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