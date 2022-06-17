import { Token, TokenType } from "../../../../lexer/tokens/Token";
import { ScriptReader } from "../../../logic/ScriptReader";
import { StatementContents } from "../StatementContents";
import * as vscode from "vscode";
import { GSCUtil } from "../../../../util/GSCUtil";
import { ScriptDependency } from "../../../data/ScriptDependency";
import { TokenRule } from "../../../logic/TokenRule";
import { OperatorType } from "../../../../lexer/tokens/types/Operator";
import { SpecialTokenTypes } from "../../../../lexer/tokens/types/SpecialToken";

export class FunctionDeclArgExpression extends StatementContents {
    name?: string;
	// TODO: Change to an expression type explicitly
	default?: StatementContents;

    /**
     * Parses the given statement contents, which may include recursive calls.
     * A function argument will recurse through an expression if there is a default value specified.
     */
    parse(reader: ScriptReader): void {
		// Read name then advance the reader
        let nameToken = reader.readToken();
		reader.index++;
		if(nameToken.getType() === TokenType.Name) {
			this.name = (<Token> nameToken).contents;
		} else {
			reader.diagnostic.pushDiagnostic(nameToken.getLocation(), "Token error: expected argument name");
			return;
		}

		// Check if there is a default value
        let token = reader.readToken();
		let defaultMatcher = new TokenRule(TokenType.Operator, OperatorType.Assignment);

		if(defaultMatcher.matches(token)) {
			// There is a default value, advance the reader
			reader.index++;

			// Parse the default value expression
			// (TODO)
			reader.index++;
		}

		// Done
    }
}