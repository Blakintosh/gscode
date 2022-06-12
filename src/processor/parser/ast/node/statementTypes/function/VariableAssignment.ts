import { Token, TokenType } from "../../../../../lexer/tokens/Token";
import { OperatorType } from "../../../../../lexer/tokens/types/Operator";
import { ScriptReader } from "../../../../logic/ScriptReader";
import { TokenRule } from "../../../../logic/TokenRule";
import { StatementContents } from "../../../expression/StatementContents";
import { FunctionDeclArgsExpression } from "../../../expression/types/FunctionDeclArgsExpression";
import { StatementNode } from "../../StatementNode";

export class VariableAssignment extends StatementNode {
	arguments: FunctionDeclArgsExpression = new FunctionDeclArgsExpression();
	name?: string;
	isDeclaration: boolean = false;

    getContents(): StatementContents {
        throw new Error("Method not implemented.");
    }

    getRule(): TokenRule[] {
        return [
			new TokenRule(TokenType.Name),
            new TokenRule(TokenType.Operator, OperatorType.Assignment)
        ];
    }

	parse(reader: ScriptReader): void {
		// Get variable name and validate it
		this.name = (<Token> reader.readToken()).contents;
		reader.index++;

		// no validation needed on =
		reader.index++;

		// Parse the variable assignment expression
		reader.index++; // TODO

		// As with every statement
		super.parse(reader);
	}
}