import {Token} from "../tokens/Token";
import {Branch} from "./Branch";
import {Statement} from "./Statement";

export class RootBranch extends Branch {
	fileName: string;

	constructor(token: Token, fileName: string, contents: Array<Statement>, start: number, end: number) {
		super(token, contents, start, end);
		this.fileName = fileName;
	}
}