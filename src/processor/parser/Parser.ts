import * as vscode from "vscode";
import { Lexer } from "../lexer/Lexer";
import { BranchNode } from "./ast/node/BranchNode";
import { UsingDirective } from "./ast/node/statementTypes/preprocessor/UsingDirective";
import { ScriptDependency } from "./data/ScriptDependency";
import { ParserDiagnostics } from "./diagnostics/ParserDiagnostics";
import { ScriptReader } from "./logic/ScriptReader";
import { ScriptSemanticToken } from "./ScriptSemanticToken";

export class Parser {
    readonly lexer: Lexer;
    rootNode: BranchNode = new BranchNode();
    // Used during analysis
    reader: ScriptReader;
	// Associated file data
    readonly diagnostic: ParserDiagnostics;
	private dependencies: ScriptDependency[] = [];
	private semanticTokens: ScriptSemanticToken[] = [];

    constructor(document: vscode.TextDocument, lexer: Lexer) {
        this.lexer = lexer;
        this.diagnostic = new ParserDiagnostics(document);
        this.reader = new ScriptReader(this.lexer.tokens, this.dependencies, this.diagnostic, this.semanticTokens);
    }

	postParse(): void {
		// Check uses of dependencies
		for(let dependency of this.dependencies) {
			if(dependency.uses === 0) {
				this.diagnostic.pushDiagnostic(dependency.location, "Using directive is unnecessary.", vscode.DiagnosticSeverity.Hint, [vscode.DiagnosticTag.Unnecessary]);
			}
		}
	}

    parse(): void {
        this.rootNode.parse(this.reader, [
			new UsingDirective()
		]);

		this.postParse();
    }
}