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

import * as vscode from "vscode";
import { ScriptReader } from "../../logic/ScriptReader";

export interface IASTNode {
    /**
     * Gets the children of this node. This could be a sequence of statements,
     * or just a single branch, depending on context.
	 * An empty array signals no children.
     */
    getChildren(): IASTNode[];

    /**
     * Returns whether the sequence of tokens at this current position matches this node.
     * @param reader Reference to the token reader.
     */
    matches(reader: ScriptReader): boolean;

	/**
     * Parses the given node, which may include recursion calls.
     */
	parse(reader: ScriptReader, allowedChildrenFunc: (() => IASTNode[]) | undefined): void;
}