import { IToken } from "../../lexer/tokens/IToken";
import { ScriptDependency } from "../data/ScriptDependency";
import { ScriptSemanticToken } from "../ScriptSemanticToken";
import { ParserDiagnostics } from "../diagnostics/ParserDiagnostics";
import * as vscode from "vscode";
import path = require("path");

export class ScriptReader {
    index: number = 0;
    readonly tokens: IToken[];
	readonly diagnostic: ParserDiagnostics;
	dependencies: ScriptDependency[];
	semanticTokens: ScriptSemanticToken[];
	readonly format: string;
	currentNamespace: string;

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
        return this.tokens[this.index + offset];
    }

	readAhead(): IToken {
		return this.readToken(1);
	}

	atEof(): boolean {
		return this.index >= this.tokens.length;
	}
}