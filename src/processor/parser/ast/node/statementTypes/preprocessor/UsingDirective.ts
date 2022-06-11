import { TokenType } from "../../../../../lexer/tokens/Token";
import { KeywordTypes } from "../../../../../lexer/tokens/types/Keyword";
import { ScriptDependency } from "../../../../data/ScriptDependency";
import { ScriptReader } from "../../../../logic/ScriptReader";
import { TokenRule } from "../../../../logic/TokenRule";
import { StatementContents } from "../../../expression/StatementContents";
import { FilePathExpression } from "../../../expression/types/FilePathExpression";
import { StatementNode } from "../../StatementNode";

export class UsingDirective extends StatementNode {
    file: FilePathExpression = new FilePathExpression();

    getContents(): StatementContents {
        return this.file;
    }

    getRule(): TokenRule[] {
        return [
            new TokenRule(TokenType.Keyword, KeywordTypes.Using)
        ];
    }

	parse(reader: ScriptReader): void {
		// No further validation required on the keyword, advance by one token.
		reader.index++;

		// Parse the file path expression.
		this.file.parse(reader);

		// Add this as a dependency
		if(this.file.filePath && this.file.location) {
			reader.dependencies.push(new ScriptDependency(this.file.filePath, this.file.location));
		}

		// Use at the end of every subclass of a statement node.
		super.parse(reader);
	}
}