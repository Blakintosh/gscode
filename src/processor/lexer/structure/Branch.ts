import { IToken } from "../interfaces/IToken";
import {Token} from "../tokens/Token";
import {Statement} from "./Statement";

import {Conditional} from "../tokens/Conditional";
import { Expression } from "../tokens/Expression";
import { Reference } from "../tokens/Reference";
import { FunctionCall } from "../tokens/FunctionCall";

export class Branch extends Statement {
	/**
	 * Array of statements in order of occurrence within this branch
	 */
	contents: Array<Statement>;

	/**
	 * Whether or not this branch only extends to one statement (i.e. no use of braces)
	 */
	oneStatement: boolean;

	constructor(token: Token, contents: Array<Statement>, start: number, end: number, oneStatement=false) {
		super(token, start, end);
		this.contents = contents;
		this.oneStatement = oneStatement;
	}

	analyseBranch(text: String, startPosition: number): number {
		let position = startPosition;
		let endChar = (this.oneStatement ? ';' : '}');

		// Tokens we would expect to see in the average branch
		let potentialTokens: Array<IToken> = [
			new FunctionCall()
		];

		while(text.charAt(position) !== endChar) {
			for(let i = 0; i < potentialTokens.length; i++) {
				let element = potentialTokens[i];
				if(element.tokenMatches(text.substring(position), position)) {
					console.log("yeah we found one and what ??");
					break;
				}
			}
		}
		return startPosition;
	}
}