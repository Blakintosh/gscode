import {Token} from "../tokens/Token";

export class Statement {
	token: Token;
	start: number;
	end: number;

	constructor(token: Token, start: number, end: number) {
		this.token = token;
		this.start = start;
		this.end = end;
	}
}