import { Token, TokenType } from "../../../../lexer/tokens/Token";
import { TokenReader } from "../../../logic/TokenReader";
import { StatementContents } from "../StatementContents";
import * as vscode from "vscode";
import { GSCUtil } from "../../../../util/GSCUtil";

export class FilePathExpression extends StatementContents {
    filePath?: string;

    /**
     * Parses the given statement contents, which may include recursive calls.
     * For file path there are no nested calls.
     */
    parse(reader: TokenReader): void {
        let token = reader.readToken();
        let location = token.getLocation();

        // Check token could be a path
        if(token.getType() !== TokenType.Name) {
            // This token can't be a file path, abort parsing
            super.pushDiagnostic(location, "Token error: expected file path");
            reader.index++;
            return;
        }

        // Check if this path is valid
        const path = (<Token> token).contents;
        if(!GSCUtil.validateScriptPath(path)) {
            // We only want to warn if a path is not found, as maybe they have it elsewhere
            super.pushDiagnostic(location, "Path warning: unable to locate script "+path, vscode.DiagnosticSeverity.Warning);
        }

        super.pushSemantic(location, "string");
        this.filePath = path;
        reader.index++; // Done
    }
}