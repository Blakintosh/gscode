import { DiagnosticSeverity } from "vscode";
import { TokenType } from "../../../lexer/tokens/Token";
import { PunctuationTypes } from "../../../lexer/tokens/types/Punctuation";
import { ScriptDiagnostic } from "../../diagnostics/ScriptDiagnostic";
import { TokenReader } from "../../logic/TokenReader";
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
     * Parses the next statement in this branch.
     * @param parser Reference to the token reader.
     * @param allowedChildren The child nodes allowed in this branch.
     */
    private parseNextNode(parser: TokenReader, allowedChildren: IASTNode[]): IASTNode {
        for(const child of allowedChildren) {
            if(child.matches(parser)) {
                //child.parse(parser);
                return child;
            }
        }
        throw new Error("Not implemented");
    }

    /**
     * Reads the next token to see if the end of the branch has been reached (a closing brace).
     * @param parser Reference to the token reader.
     * @returns true if at end, false otherwise.
     */
    private atEndOfBranch(parser: TokenReader): boolean {
        const matcher = new TokenRule(TokenType.Punctuation, PunctuationTypes.CloseBrace);
        return matcher.matches(parser.readToken());
    }

    /**
     * Parses the given branch.
     * @param parser Reference to the token reader.
     * @param allowedChildren The child nodes allowed in this branch.
     */
    parse(parser: TokenReader, allowedChildren: IASTNode[]): void {
        if(this.oneStatement) {
            this.statements[0] = this.parseNextNode(parser, allowedChildren);
        } else {
            while(!this.atEndOfBranch(parser)) {
                const pos = this.statements.length;
                this.statements[pos] = this.parseNextNode(parser, allowedChildren);
            }
        }
    }

    /**
     * A branch node does not get matched.
     */
    matches(parser: TokenReader): boolean {
        return false;
    }
}