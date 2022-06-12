/* eslint-disable @typescript-eslint/naming-convention */
import { ScriptDiagnostic } from "../../diagnostics/ScriptDiagnostic";
import { TokenRule } from "../../logic/TokenRule";
import { ScriptSemanticToken } from "../../ScriptSemanticToken";
import { StatementContents } from "../expression/StatementContents";
import { IASTNode } from "./IASTNode";
import * as vscode from "vscode";
import { ScriptReader } from "../../logic/ScriptReader";
import { PunctuationTypes } from "../../../lexer/tokens/types/Punctuation";
import { TokenType } from "../../../lexer/tokens/Token";
import { BranchNode } from "./BranchNode";
import { SpecialTokenTypes } from "../../../lexer/tokens/types/SpecialToken";

/**
 * Abstract class that all statements are derived from.
 * A StatementNode follows the following rules:
 * [Directive] (Contents);
 * or [Directive] (Contents) {Branch}
 * (Contents) is not necessarily in parenthesis, and this depends on the choice of Expression for the contents.
 */
export abstract class StatementNode implements IASTNode {
    diagnostics: ScriptDiagnostic[] = [];
    semantics: ScriptSemanticToken[] = [];
    child?: IASTNode;
	// For subclasses that open branches: these should be defined in their constructor
    expectsBranch: boolean = false;
    expectedChildren?: IASTNode[];

    /**
     * Gets the child Branch of this statement if it exists.
     * @returns An empty or 1 element IASTNode array that contains a branch if applies.
     */
    getChildren(): IASTNode[] {
        return (this.child ? [this.child] : []);
    }

    /**
     * Pushes a diagnostic to the statement's diagnostic array.
     * @param location The location of the token/diagnostic
     * @param message The message for this diagnostic
     * @param severity The severity
     */
    pushDiagnostic(location: [number, number], message: string, severity: vscode.DiagnosticSeverity | undefined = undefined) {
        this.diagnostics.push(new ScriptDiagnostic(location, message, severity));
    }

    /**
     * Pushes this token onto the statement's semantic array.
     * @param location The location of the token/semantic
     * @param tokenType The type of token
     * @param tokenModifiers An array of modifiers for the semantic
     */
    pushSemantic(location: [number, number], tokenType: string, tokenModifiers: string[] | undefined = undefined): void {
        this.semantics.push(new ScriptSemanticToken(location, tokenType, tokenModifiers));
    }

    /**
     * Gets whether this statement matches the current token sequence.
     * @param parser Reference to the token reader.
     * @returns true if matches, false otherwise.
     */
    matches(parser: ScriptReader): boolean {
        const rule = this.getRule();
        for(let i = 0; i < rule.length; i++) {
            if(!rule[i].matches(parser.readToken(i))) {
                return false;
            }
        }
        return true;
    }

    /**
     * Gets the parameter contents of this statement.
     */
    abstract getContents(): StatementContents;

    /**
     * Gets the expected tokens for this statement (not including parameter contents).
     * Example: function test() would be [TokenRule(Keyword, Function), TokenRule(Name)]
     */
    abstract getRule(): TokenRule[];

	/**
     * Parses the given statement, which may include recursion calls.
	 * The Superclass parse handles branching
     */
	parse(reader: ScriptReader): void {
		if(this.expectsBranch) {
			// Parse branch
			let nextToken = reader.readToken();
			let openBranch = new TokenRule(TokenType.Punctuation, PunctuationTypes.OpenBrace);

			let oneStatement = !openBranch.matches(nextToken);

			// Advance the reader to the next token if we have a multiline branch
			if(!oneStatement) {
				reader.index++;
			}

			this.child = new BranchNode(oneStatement);

			// Parse the branch with the children types expected, as defined in the subclass
			this.child.parse(reader, this.expectedChildren);
		} else {
			// Check and verify a semicolon has been used
			let nextToken = reader.readToken();
			let semicolon = new TokenRule(TokenType.SpecialToken, SpecialTokenTypes.EndStatement);

			if(!semicolon.matches(nextToken)) {
				// Throw an error at the end of the last statement.
				reader.diagnostic.pushDiagnostic(reader.readToken(-1).getLocation(), "Token error: missing semicolon");
			} else {
				reader.index++;
			}
		}
	}
}