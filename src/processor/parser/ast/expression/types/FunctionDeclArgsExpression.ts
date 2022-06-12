import { Token, TokenType } from "../../../../lexer/tokens/Token";
import { ScriptReader } from "../../../logic/ScriptReader";
import { StatementContents } from "../StatementContents";
import * as vscode from "vscode";
import { GSCUtil } from "../../../../util/GSCUtil";
import { ScriptDependency } from "../../../data/ScriptDependency";
import { PunctuationTypes } from "../../../../lexer/tokens/types/Punctuation";
import { TokenRule } from "../../../logic/TokenRule";
import { FunctionDeclArgExpression } from "./FunctionDeclArgExpression";
import { SpecialTokenTypes } from "../../../../lexer/tokens/types/SpecialToken";

export class FunctionDeclArgsExpression extends StatementContents {
    arguments: FunctionDeclArgExpression[] = [];

    /**
     * Parses the given statement contents, which may include recursive calls.
     * Each argument of a function declaration will individually be recursed to parse.
     */
    parse(reader: ScriptReader): void {
        let token = reader.readToken();
		let startOfArgs = new TokenRule(TokenType.Punctuation, PunctuationTypes.OpenParen);
		let endOfArgs = new TokenRule(TokenType.Punctuation, PunctuationTypes.CloseParen);

		if(startOfArgs.matches(token)) {
			reader.index++;
			while(!endOfArgs.matches(reader.readToken())) {
				// Advance to current argument
				let argument = new FunctionDeclArgExpression();
				argument.parse(reader);

				let comma = new TokenRule(TokenType.SpecialToken, SpecialTokenTypes.Comma);
				let nextToken = reader.readToken();
				if(!comma.matches(nextToken) && !endOfArgs.matches(nextToken)) {
					reader.diagnostic.pushDiagnostic(nextToken.getLocation(), "Token error: expected comma or closing parenthesis");
					reader.index++;
					return;
				} else if(comma.matches(nextToken)) {
					reader.index++;
				}
			}
		}

        reader.index++; // Done
    }
}