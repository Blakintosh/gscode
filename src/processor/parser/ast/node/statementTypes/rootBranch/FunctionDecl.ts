import { TokenType } from "../../../../../lexer/tokens/Token";
import { KeywordTypes } from "../../../../../lexer/tokens/types/Keyword";
import { ScriptDependency } from "../../../../data/ScriptDependency";
import { ScriptReader } from "../../../../logic/ScriptReader";
import { TokenRule } from "../../../../logic/TokenRule";
import { StatementContents } from "../../../expression/StatementContents";
import { FilePathExpression } from "../../../expression/types/FilePathExpression";
import { FunctionDeclArgsExpression } from "../../../expression/types/FunctionDeclArgsExpression";
import { StatementNode } from "../../StatementNode";
import { VariableAssignment } from "../function/VariableAssignment";

export class FunctionDecl extends StatementNode {
    //file: FilePathExpression = new FilePathExpression();
	arguments: FunctionDeclArgsExpression = new FunctionDeclArgsExpression();

	constructor() {
		super();
		// A function declaration is a branching statement node
		super.expectsBranch = true;
		super.expectedChildren = [
			new VariableAssignment()
		];
	}

    getContents(): StatementContents {
        throw new Error("Method not implemented.");
    }

    getRule(): TokenRule[] {
        return [
            new TokenRule(TokenType.Keyword, KeywordTypes.Function),
			new TokenRule(TokenType.Name)
        ];
    }

	parse(reader: ScriptReader): void {
		// Store keyword position
		let keywordPosition = reader.readToken().getLocation();

		// No further validation required on the keyword, advance by one token.
		reader.index++;

		// Validate the function name and add it to the function names list.
		reader.index++;

		// Parse the function arguments expression.
		this.arguments.parse(reader); 

		// Parse the file path expression.
		//this.file.parse(reader);

		// Add this as a dependency
		//if(this.file.filePath && this.file.location) {
		//	reader.dependencies.push(new ScriptDependency(this.file.filePath, [keywordPosition[0], this.file.location[1]]));
		//}

		// Use at the end of every subclass of a statement node.
		super.parse(reader);
	}
}