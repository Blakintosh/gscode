import { IToken } from "../../lexer/tokens/IToken";

export class TokenReader {
    index: number = 0;
    readonly tokens: IToken[];

    constructor(tokens: IToken[]) {
        this.tokens = tokens;
    }

    readToken(offset: number = 0): IToken {
        return this.tokens[this.index + offset];
    }
}