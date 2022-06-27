import { TokenType } from "../../../../../lexer/tokens/Token";
import { KeywordTypes } from "../../../../../lexer/tokens/types/Keyword";
import { ScriptDependency } from "../../../../data/ScriptDependency";
import { ScriptReader } from "../../../../logic/ScriptReader";
import { TokenRule } from "../../../../logic/TokenRule";
import { StatementContents } from "../../../expression/StatementContents";
import { NameExpression } from "../../../expression/types/NameExpression";
import { StatementNode } from "../../StatementNode";

export class NamespaceDirective extends StatementNode {
    namespace: NameExpression = new NameExpression();

    getContents(): StatementContents {
        return this.namespace;
    }

    getRule(): TokenRule[] {
        return [
            new TokenRule(TokenType.Keyword, KeywordTypes.Namespace)
        ];
    }

	parse(reader: ScriptReader): void {
		// Store keyword position
		let keywordPosition = reader.readToken().getLocation();

		// No further validation required on the keyword, advance by one token.
		reader.index++;

		// Parse the file path expression.
		this.namespace.parse(reader);

		// Get semicolon if it exists.
		const semicolon = super.getSemicolonToken(reader);

		// Set reader's current namespace to this namespace
		if(this.namespace.value) {
			reader.currentNamespace = this.namespace.value;
		}

		// Use at the end of every subclass of a statement node.
		super.parse(reader);
	}
}