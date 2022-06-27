import { TokenType } from "../../../../../lexer/tokens/Token";
import { KeywordTypes } from "../../../../../lexer/tokens/types/Keyword";
import { ScriptDependency } from "../../../../data/ScriptDependency";
import { ScriptReader } from "../../../../logic/ScriptReader";
import { TokenRule } from "../../../../logic/TokenRule";
import { StatementContents } from "../../../expression/StatementContents";
import { FilePathExpression } from "../../../expression/types/FilePathExpression";
import { StatementNode } from "../../StatementNode";

export class InsertDirective extends StatementNode {
    file: FilePathExpression = new FilePathExpression();

    getContents(): StatementContents {
        return this.file;
    }

    getRule(): TokenRule[] {
        return [
            new TokenRule(TokenType.Keyword, KeywordTypes.Insert)
        ];
    }

	parse(reader: ScriptReader): void {
		// Store keyword position
		let keywordPosition = reader.readToken().getLocation();

		// No further validation required on the keyword, advance by one token.
		reader.index++;

		// Parse the file path expression.
		this.file.parse(reader);

		// Get semicolon if it exists.
		const semicolon = super.getSemicolonToken(reader);

		// Add this as an insertion (TODO)
		if(this.file.filePath && this.file.location) {
			const endLoc = (semicolon ? semicolon.getLocation()[1] : this.file.location[1]);

			//reader.dependencies.push(new ScriptDependency(this.file.filePath, [keywordPosition[0], endLoc]));
		}

		// Use at the end of every subclass of a statement node.
		super.parse(reader);
	}
}