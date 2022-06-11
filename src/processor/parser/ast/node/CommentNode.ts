import { DiagnosticSeverity } from "vscode";
import { TokenType } from "../../../lexer/tokens/Token";
import { ScriptReader } from "../../logic/ScriptReader";
import { IASTNode } from "./IASTNode";

export class CommentNode implements IASTNode {
    pushDiagnostic(location: [number, number], message: string, severity: DiagnosticSeverity | undefined): void {
        throw new Error("Method not implemented.");
    }
    pushSemantic(location: [number, number], tokenType: string, tokenModifiers: string[] | undefined): void {
        throw new Error("Method not implemented.");
    }
    matches(reader: ScriptReader): boolean {
        return (reader.readToken().getType() === TokenType.Comment);
    }
    /**
     * Gets the children of a comment, which never exist.
     * @returns An empty array.
     */
    getChildren(): IASTNode[] {
        return [];
    }

	/**
     * Parses the given comment, which does nothing.
     */
	parse(reader: ScriptReader): void {
		return;
	};
}