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
import { IToken } from "../../../lexer/tokens/IToken";

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
	 * After parsing a statement, gets the next token if it's a semicolon for location purposes.
	 * @param reader Reference to the token reader
	 * @returns An IToken corresponding to this statement's semi colon if it exists, undefined otherwise.
	 */
	getSemicolonToken(reader: ScriptReader): IToken | undefined {
		const nextToken = reader.readToken();
		const semiColon = new TokenRule(TokenType.SpecialToken, SpecialTokenTypes.EndStatement);

		if(semiColon.matches(nextToken)) {
			return nextToken;
		}
		return undefined;
	}

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
			const semicolon = this.getSemicolonToken(reader);

			if(!semicolon) {
				// Throw an error at the end of the last statement.
				let lastTokenLoc = reader.readToken(-1).getLocation();
				reader.diagnostic.pushDiagnostic([lastTokenLoc[1] - 1, lastTokenLoc[1]], "Token error: missing semicolon");
			} else {
				reader.index++;
			}
		}
	}
}