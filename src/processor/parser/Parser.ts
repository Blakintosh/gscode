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

import * as vscode from "vscode";
import { Lexer } from "../lexer/Lexer";
import { BranchNode } from "./ast/node/BranchNode";
import { InsertDirective } from "./ast/node/statementTypes/preprocessor/InsertDirective";
import { NamespaceDirective } from "./ast/node/statementTypes/preprocessor/NamespaceDirective";
import { UsingDirective } from "./ast/node/statementTypes/preprocessor/UsingDirective";
import { FunctionDecl } from "./ast/node/statementTypes/rootBranch/FunctionDecl";
import { ScriptDependency } from "./data/ScriptDependency";
import { ParserDiagnostics } from "./diagnostics/ParserDiagnostics";
import { ScriptReader } from "./logic/ScriptReader";
import { ScriptSemanticToken } from "./ScriptSemanticToken";
import { GSCBranchNodes } from "../util/GSCUtil";

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
        this.reader = new ScriptReader(document, this.lexer.tokens, this.dependencies, this.diagnostic, this.semanticTokens);
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
        this.rootNode.parse(this.reader, GSCBranchNodes.Root());

		this.postParse();
    }
}