import * as vscode from "vscode";
import { TokenReader } from "../../logic/TokenReader";

export interface IASTNode {
    /**
     * Gets the children of this node. This could be a sequence of statements,
     * or just a single branch, depending on context.
     */
    getChildren(): IASTNode[];

    /**
     * Pushes a diagnostic to the node's diagnostic array.
     * @param location The location of the token/diagnostic
     * @param message The message for this diagnostic
     * @param severity The severity
     */
    pushDiagnostic(location: [number, number], message: string, severity: vscode.DiagnosticSeverity | undefined): void;

    /**
     * Pushes this token onto the node's semantic array.
     * @param location The location of the token/semantic
     * @param tokenType The type of token
     * @param tokenModifiers An array of modifiers for the semantic
     */
    pushSemantic(location: [number, number], tokenType: string, tokenModifiers: string[] | undefined): void;


    /**
     * Returns whether the sequence of tokens at this current position matches this node.
     * @param reader Reference to the token reader.
     */
    matches(reader: TokenReader): boolean;
}