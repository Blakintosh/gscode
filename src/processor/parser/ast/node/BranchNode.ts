/**
	GSCode Language Extension for Visual Studio Code
    Copyright (C) 2022 Blakintosh

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

import { TokenType } from "../../../lexer/tokens/Token";
import { PunctuationTypes } from "../../../lexer/tokens/types/Punctuation";
import { GSCProcessNames } from "../../../util/GSCUtil";
import { ScriptReader } from "../../logic/ScriptReader";
import { TokenRule } from "../../logic/TokenRule";
import { IASTNode } from "./IASTNode";

export class BranchNode implements IASTNode {
    statements: IASTNode[] = [];
    oneStatement: boolean;

    constructor(oneStatement: boolean = false) {
        this.oneStatement = oneStatement;
    }

    /**
     * Gets the statements within this branch.
     * @returns An array of IASTNode statements that belong to this branch.
     */
    getChildren(): IASTNode[] {
        return this.statements;
    }

	/**
	 * Gets whether no statements validate at this index.
	 * @param parser Reference to the token reader.
	 * @param allowedChildren The child nodes allowed in this branch.
	 * @returns true if so, false otherwise
	 */
	private noValidNextNode(parser: ScriptReader, allowedChildren: IASTNode[]): boolean {
		for(const child of allowedChildren) {
			if(child.matches(parser)) {
				return false;
			}
		}
		return true;
	}

    /**
     * Parses the next statement in this branch.
     * @param parser Reference to the token reader.
     * @param allowedChildren The child nodes allowed in this branch.
     */
    private parseNextNode(parser: ScriptReader, allowedChildren: IASTNode[]): IASTNode | null {
        for(const child of allowedChildren) {
            if(child.matches(parser)) {
                child.parse(parser, undefined);
                return child;
            }
        }

		// No valid child found, mark as error until next valid directive found.
		const firstFailedPosition = parser.readToken().getLocation();

		// Increment the parser through unrecognised tokens until we reach the end of the file, the end of the branch, or we find a valid node
		do {
			parser.index++;
		} while(!parser.atEof() && !this.atEndOfBranch(parser) && this.noValidNextNode(parser, allowedChildren));

		// Get the token before the current index to end the error on.
		const lastFailedPosition = parser.readToken(-1).getLocation();

		// Altho. evaluated at parser stage, this is an error where the lexer couldn't resolve this token
		parser.diagnostic.pushDiagnostic([firstFailedPosition[0], lastFailedPosition[1]], "Unrecognised token.", GSCProcessNames.Lexer);

		return null;
    }

    /**
     * Reads the next token to see if the end of the branch has been reached (a closing brace).
     * @param parser Reference to the token reader.
     * @returns true if at end, false otherwise.
     */
    private atEndOfBranch(parser: ScriptReader): boolean {
        const matcher = new TokenRule(TokenType.Punctuation, PunctuationTypes.CloseBrace);
        return matcher.matches(parser.readToken());
    }

    /**
     * Parses the given branch.
     * @param parser Reference to the token reader.
     * @param allowedChildren The child nodes allowed in this branch.
     */
    parse(parser: ScriptReader, allowedChildren: IASTNode[]): void {
        if(this.oneStatement) {
			let nextChild = this.parseNextNode(parser, allowedChildren);
			if(nextChild !== null) {
				this.statements[0] = nextChild;
			}
        } else {
            while(!parser.atEof() && !this.atEndOfBranch(parser)) {
                const pos = this.statements.length;
				let nextChild = this.parseNextNode(parser, allowedChildren);
				if(nextChild !== null) {
					this.statements[pos] = nextChild;
				}
            }
			// Advance by one token if we haven't reached the end of the file
			if(!parser.atEof()) {
				parser.index++;
			}
        }
    }

    /**
     * A branch node does not get matched.
     */
    matches(parser: ScriptReader): boolean {
        return false;
    }
}