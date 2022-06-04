import { TokenType } from "../../../../../lexer/tokens/Token";
import { KeywordTypes } from "../../../../../lexer/tokens/types/Keyword";
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

}