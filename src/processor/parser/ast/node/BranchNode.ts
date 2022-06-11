import { DiagnosticSeverity } from "vscode";
import { TokenType } from "../../../lexer/tokens/Token";
import { PunctuationTypes } from "../../../lexer/tokens/types/Punctuation";
import { ScriptDiagnostic } from "../../diagnostics/ScriptDiagnostic";
import { ScriptReader } from "../../logic/ScriptReader";
import { TokenRule } from "../../logic/TokenRule";
import { ScriptSemanticToken } from "../../ScriptSemanticToken";
import { IASTNode } from "./IASTNode";
import * as vscode from "vscode";

export class BranchNode implements IASTNode {
    diagnostics: ScriptDiagnostic[] = [];
    semantics: ScriptSemanticToken[] = [];
    statements: IASTNode[] = [];
    oneStatement: boolean;

    constructor(oneStatement: boolean = false) {
        this.oneStatement = oneStatement;
    }

    /**
     * Pushes a diagnostic to the branch's diagnostic array.
     * @param location The location of the token/diagnostic
     * @param message The message for this diagnostic
     * @param severity The severity
     */
    pushDiagnostic(location: [number, number], message: string, severity: vscode.DiagnosticSeverity | undefined = undefined) {
        this.diagnostics.push(new ScriptDiagnostic(location, message, severity));
    }

    /**
     * Pushes this token onto the branch's semantic array.
     * @param location The location of the token/semantic
     * @param tokenType The type of token
     * @param tokenModifiers An array of modifiers for the semantic
     */
    pushSemantic(location: [number, number], tokenType: string, tokenModifiers: string[] | undefined = undefined): void {
        this.semantics.push(new ScriptSemanticToken(location, tokenType, tokenModifiers));
    }

    /**
     * Gets the statements within this branch.
     * @returns An array of IASTNode statements that belong to this branch.
     */
    getChildren(): IASTNode[] {
        return this.statements;
    }

	/**
	 * Gets whether no statements validate at this index.
	 * @param parser Reference to the token reader.
	 * @param allowedChildren The child nodes allowed in this branch.
	 * @returns true if so, false otherwise
	 */
	private noValidNextNode(parser: ScriptReader, allowedChildren: IASTNode[]): boolean {
		for(const child of allowedChildren) {
			if(child.matches(parser)) {
				return false;
			}
		}
		return true;
	}

    /**
     * Parses the next statement in this branch.
     * @param parser Reference to the token reader.
     * @param allowedChildren The child nodes allowed in this branch.
     */
    private parseNextNode(parser: ScriptReader, allowedChildren: IASTNode[]): IASTNode | null {
        for(const child of allowedChildren) {
            if(child.matches(parser)) {
                child.parse(parser, undefined);
                return child;
            }
        }

		// No valid child found, mark as error until next valid directive found.
		const firstFailedPosition = parser.readToken().getLocation();

		// Increment the parser through unrecognised tokens until we reach the end of the file, the end of the branch, or we find a valid node
		do {
			parser.index++;
		} while(!parser.atEof() && !this.atEndOfBranch(parser) && !this.noValidNextNode(parser, allowedChildren));

		// Get the token before the current index to end the error on.
		const lastFailedPosition = parser.readToken(-1).getLocation();

		parser.diagnostic.pushDiagnostic([firstFailedPosition[0], lastFailedPosition[1]], "Token error: unrecognised token");

		return null;
    }

    /**
     * Reads the next token to see if the end of the branch has been reached (a closing brace).
     * @param parser Reference to the token reader.
     * @returns true if at end, false otherwise.
     */
    private atEndOfBranch(parser: ScriptReader): boolean {
        const matcher = new TokenRule(TokenType.Punctuation, PunctuationTypes.CloseBrace);
        return matcher.matches(parser.readToken());
    }

    /**
     * Parses the given branch.
     * @param parser Reference to the token reader.
     * @param allowedChildren The child nodes allowed in this branch.
     */
    parse(parser: ScriptReader, allowedChildren: IASTNode[]): void {
        if(this.oneStatement) {
			let nextChild = this.parseNextNode(parser, allowedChildren);
			if(nextChild !== null) {
				this.statements[0] = nextChild;
			}
        } else {
            while(!parser.atEof() && !this.atEndOfBranch(parser)) {
                const pos = this.statements.length;
				let nextChild = this.parseNextNode(parser, allowedChildren);
				if(nextChild !== null) {
					this.statements[pos] = nextChild;
				}
            }
        }
    }

    /**
     * A branch node does not get matched.
     */
    matches(parser: ScriptReader): boolean {
        return false;
    }
}