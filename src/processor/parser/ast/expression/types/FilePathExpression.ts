import { Token, TokenType } from "../../../../lexer/tokens/Token";
import { ScriptReader } from "../../../logic/ScriptReader";
import { StatementContents } from "../StatementContents";
import * as vscode from "vscode";
import { GSCUtil } from "../../../../util/GSCUtil";
import { ScriptDependency } from "../../../data/ScriptDependency";

export class FilePathExpression extends StatementContents {
    filePath?: string;
	location?: [number, number];

    /**
     * Parses the given statement contents, which may include recursive calls.
     * For file path there are no nested calls.
     */
    parse(reader: ScriptReader): void {
        let token = reader.readToken();

        // Check token could be a path
        if(token.getType() !== TokenType.Name) {
            // This token can't be a file path, abort parsing
            reader.diagnostic.pushDiagnostic(token.getLocation(), "Token error: expected file path");
            reader.index++;
            return;
        }
		
		// Store the location as it validates
        this.location = token.getLocation();

        // Check if this path is valid
        const path = (<Token> token).contents;
        if(!GSCUtil.validateScriptPath(path + ".gsc")) {
            // We only want to warn if a path is not found, as maybe they have it elsewhere
            reader.diagnostic.pushDiagnostic(this.location, "Path warning: unable to locate script "+path+".gsc", vscode.DiagnosticSeverity.Warning);
        }

        this.filePath = path;

        reader.index++; // Done
    }
}