import { IToken } from "../../lexer/tokens/IToken";
import { ScriptDependency } from "../data/ScriptDependency";
import { ScriptSemanticToken } from "../ScriptSemanticToken";
import { ParserDiagnostics } from "../diagnostics/ParserDiagnostics";

export class ScriptReader {
    index: number = 0;
    readonly tokens: IToken[];
	readonly diagnostic: ParserDiagnostics;
	dependencies: ScriptDependency[];
	semanticTokens: ScriptSemanticToken[];

    constructor(tokens: IToken[], dependencies: ScriptDependency[], diagnostic: ParserDiagnostics, semanticTokens: ScriptSemanticToken[]) {
        this.tokens = tokens;
		this.dependencies = dependencies;
		this.diagnostic = diagnostic;
		this.semanticTokens = semanticTokens;
    }

    readToken(offset: number = 0): IToken {
        return this.tokens[this.index + offset];
    }

	atEof(): boolean {
		return this.index >= this.tokens.length;
	}
}