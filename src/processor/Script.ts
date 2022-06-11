import { Lexer } from "./lexer/Lexer";
import { Parser } from "./parser/Parser";
import * as vscode from "vscode";

export class Script {
	busy: boolean = true;
	private lexer: Lexer;
	private parser: Parser;

	async parse(): Promise<void> {
		this.lexer.tokenize();
		this.parser.parse();

		this.busy = false;
	}

	constructor(document: vscode.TextDocument) {
		this.busy = true;

		this.lexer = new Lexer(document);
		this.parser = new Parser(document, this.lexer);
	}
}