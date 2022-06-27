import { Token, TokenType } from "../../../../lexer/tokens/Token";
import { ScriptReader } from "../../../logic/ScriptReader";
import { StatementContents } from "../StatementContents";
import * as vscode from "vscode";
import { GSCUtil } from "../../../../util/GSCUtil";
import { ScriptDependency } from "../../../data/ScriptDependency";

export class NameExpression extends StatementContents {
	value?: string;
	location?: [number, number];

    /**
     * Parses the given statement contents, which may include recursive calls.
     * For a name there are no nested calls.
     */
    parse(reader: ScriptReader): void {
        let token = reader.readToken();

        // Check token could be a path
        if(token.getType() !== TokenType.Name) {
            // This token can't be a name, abort parsing
            reader.diagnostic.pushDiagnostic(token.getLocation(), "Token error: expected name");
            reader.index++;
            return;
        }

		this.value = (<Token> token).contents;
		
		// Store the location as it validates
        this.location = token.getLocation();

        reader.index++; // Done
    }
}