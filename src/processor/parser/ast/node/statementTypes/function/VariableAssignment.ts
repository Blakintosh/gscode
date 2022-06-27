import { Token, TokenType } from "../../../../../lexer/tokens/Token";
import { OperatorType } from "../../../../../lexer/tokens/types/Operator";
import { ScriptVariable } from "../../../../data/ScriptVariable";
import { ScriptReader } from "../../../../logic/ScriptReader";
import { TokenRule } from "../../../../logic/TokenRule";
import { StatementContents } from "../../../expression/StatementContents";
import { FunctionDeclArgsExpression } from "../../../expression/types/FunctionDeclArgsExpression";
import { StatementNode } from "../../StatementNode";

/**
 * AST Class for Variable Assignments
 * As GSC has no keyword for variable declaration, we need to treat both declaration and assignment in the same class,
 * even though there will also be a variable assignment expression
 */
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

		// Get if this variable is a new declaration
		this.isDeclaration = reader.getVar(this.name) === undefined;

		reader.index++;

		// no validation needed on =
		reader.index++;

		// Parse the variable assignment expression
		reader.index++; // TODO

		// Push to the variable stack
		if(this.isDeclaration) {
			reader.pushVar(new ScriptVariable(this.name));
		}

		console.log("Is variable "+this.name+" new? "+this.isDeclaration);

		// As with every statement
		super.parse(reader);
	}
}