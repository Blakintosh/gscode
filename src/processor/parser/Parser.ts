import * as vscode from "vscode";
import { Lexer } from "../lexer/Lexer";
import { BranchNode } from "./ast/node/BranchNode";
import { ParserDiagnostics } from "./diagnostics/ParserDiagnostics";
import { TokenReader } from "./logic/TokenReader";

export class Parser {
    readonly lexer: Lexer;
    readonly diagnostic: ParserDiagnostics;
    rootNode: BranchNode = new BranchNode();
    // Used during analysis
    reader: TokenReader;

    constructor(document: vscode.TextDocument, lexer: Lexer) {
        this.lexer = lexer;
        this.diagnostic = new ParserDiagnostics(document);
        this.reader = new TokenReader(this.lexer.tokens);
    }

    parse(): void {
        
    }
}