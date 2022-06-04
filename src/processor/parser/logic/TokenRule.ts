import { IToken } from "../../lexer/tokens/IToken";
import { TokenType } from "../../lexer/tokens/Token";

export class TokenRule {
    type: TokenType;
    // Here the "string" should actually be a string enum
    specificType?: string;

    constructor(type: TokenType, specificType: string | undefined = undefined) {
        this.type = type;
        this.specificType = specificType;
    }

    matches(token: IToken): boolean {
        if(this.type !== token.getType()) {
            return false;
        } else if(this.specificType !== undefined && this.specificType !== token.getSpecificType()) {
            return false;
        }
        return true;
    }
}