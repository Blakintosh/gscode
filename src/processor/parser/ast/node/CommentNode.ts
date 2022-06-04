import { DiagnosticSeverity } from "vscode";
import { TokenType } from "../../../lexer/tokens/Token";
import { TokenReader } from "../../logic/TokenReader";
import { IASTNode } from "./IASTNode";

export class CommentNode implements IASTNode {
    pushDiagnostic(location: [number, number], message: string, severity: DiagnosticSeverity | undefined): void {
        throw new Error("Method not implemented.");
    }
    pushSemantic(location: [number, number], tokenType: string, tokenModifiers: string[] | undefined): void {
        throw new Error("Method not implemented.");
    }
    matches(reader: TokenReader): boolean {
        return (reader.readToken().getType() === TokenType.Comment);
    }
    /**
     * Gets the children of a comment, which never exist.
     * @returns An empty array.
     */
    getChildren(): IASTNode[] {
        return [];
    }
}