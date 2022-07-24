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

/* eslint-disable @typescript-eslint/naming-convention */
import * as fs from "fs";
import * as fsPath from "path";
import * as vscode from "vscode";
import { ElseIfStatement } from "../parser/ast/node/types/function/ElseIfStatement";
import { ElseStatement } from "../parser/ast/node/types/function/ElseStatement";
import { IfStatement } from "../parser/ast/node/types/function/IfStatement";
import { ReturnStatement } from "../parser/ast/node/types/function/ReturnStatement";

// Globals
import { InsertDirective } from "../parser/ast/node/types/preprocessor/InsertDirective";
import { MacroFunctionCall } from "../parser/ast/node/types/preprocessor/MacroFunctionCall";
import { NamespaceDirective } from "../parser/ast/node/types/preprocessor/NamespaceDirective";
import { PrecacheDirective } from "../parser/ast/node/types/preprocessor/PrecacheDirective";
import { UsingDirective } from "../parser/ast/node/types/preprocessor/UsingDirective";
import { FunctionDecl } from "../parser/ast/node/types/rootBranch/FunctionDecl";
import { VariableAssignment } from "../parser/ast/node/types/function/VariableAssignment";
import { DirectFunctionCall } from "../parser/ast/node/types/function/DirectFunctionCall";
import { IASTNode } from "../parser/ast/node/IASTNode";
import { DoStatement } from "../parser/ast/node/types/function/DoStatement";
import { WhileStatement } from "../parser/ast/node/types/function/WhileStatement";
import { ForStatement } from "../parser/ast/node/types/function/ForStatement";
import { ForeachStatement } from "../parser/ast/node/types/function/ForeachStatement";
import { BreakStatement } from "../parser/ast/node/types/loop/BreakStatement";
import { ContinueStatement } from "../parser/ast/node/types/loop/ContinueStatement";
import { WaitStatement } from "../parser/ast/node/types/function/WaitStatement";

export enum FunctionFlag {
	None = 0, // All OK
	AutoGenerated = 1, // This function has not been manually verified
	Deprecated = 2, // The function has been explicitly deprecated
	Broken = 3, // The function is not the above but is broken anyway
	Useless = 4, // This function serves no use in a *modding* context
}

export enum GSCProcessNames {
	Lexer = "gscode-lex",
	Parser = "gscode-ast",
	Simulator = "gscode-sim"
}

export const GSCBranchNodes = {
	Root: function() {
		return new Array<IASTNode>(
			new UsingDirective(),
			new InsertDirective(),
			new NamespaceDirective(),
			new PrecacheDirective(),
			new FunctionDecl(),
			new MacroFunctionCall()
		);
	},
	Standard: function() {
		return new Array<IASTNode>(
			new VariableAssignment(),
			new IfStatement(),
			new ElseIfStatement(),
			new ElseStatement(),
			new DoStatement(),
			new WhileStatement(),
			new ForStatement(),
			new ForeachStatement(),
			new ReturnStatement(),
			new WaitStatement(),
			new DirectFunctionCall()
		);
	},
	Loop: function() {
		const loopStatements = new Array<IASTNode>(
			new BreakStatement(),
			new ContinueStatement()
		);

		return loopStatements.concat(GSCBranchNodes.Standard());
	}
};

export class GSCUtil {
    /**
     * Gets whether the given script path is a valid path.
     * @param path The path from TA_TOOLS_PATH or the user specified override, and this workspace.
     * @returns true if valid, false otherwise
     */
    static validateScriptPath(path: string): boolean {
        const workbenchConfig = vscode.workspace.getConfiguration('gsc');

        const validateExternals = workbenchConfig.get('dependencies.validateExternals');

        if(!validateExternals) {
            return true;
        }

        const overrideShared = workbenchConfig.get('dependencies.overrideSharedScriptsPath');

        // If we're in an open workspace, first try to find the script inside it
        const workspace = vscode.workspace.workspaceFolders;
        if(workspace !== undefined) {
            let wsFolder = workspace[0];
            let workspacePath = fsPath.normalize(wsFolder.uri.fsPath);

            // If we're not at the base folder, navigate to it
            const base = "\\scripts";
            if(workspacePath.lastIndexOf(base) !== -1) {
                workspacePath = workspacePath.substring(0, workspacePath.lastIndexOf(base));
            }

            // Now see if this script is here
			let scriptPath = fsPath.join(workspacePath, path);

            if(fs.existsSync(scriptPath)) {
                return true;
            }
        } else {
            // We don't want to perform validation if the user hasn't opened a workspace
            return true;
        }
        
        // File not found in workspace, fallback to shared path
		let toolsPath = process.env.TA_TOOLS_PATH;
		if(toolsPath !== undefined) {
			const sharePath: string = <string> (overrideShared !== "" ? overrideShared : fsPath.join(toolsPath, "/share/raw/"));

			// Check if it exists in shared
			if(fs.existsSync(fsPath.normalize(fsPath.join(sharePath, path)))) {
				return true;
			}
		} else {
			// No share path exists, so we can't validate
			return true;
		}

        // Not found
        return false;
    }

	/**
	 * Performs startup validation on GSCode's settings and configuration.
	 */
	static startupValidation(): void {
		const workbenchConfig = vscode.workspace.getConfiguration('gsc');

		// Validate that if we are checking external validity, that we have a shared path
		const overrideShared = workbenchConfig.get('dependencies.overrideSharedScriptsPath');
		const validateExternals = workbenchConfig.get('dependencies.validateExternals');

		if(overrideShared === "" && validateExternals) {
			let sharePath = process.env.TA_TOOLS_PATH;
			if(sharePath === undefined) {
				vscode.window.showErrorMessage("GSCode: No TA_TOOLS_PATH environment variable found, meaning dependency validation will not operate correctly. Please install the Black Ops III Mod Tools or set Override Shared Scripts Path in GSCode's extension settings.");
			}
		}
	}

	/**
	 * Checks whether the specified document is a script file. This does not include macro GSH files.
	 * @param document The document being checked
	 * @returns true if so, false otherwise
	 */
	static isScriptFile(document: vscode.TextDocument): boolean {
		return document.languageId === "gsc" || document.languageId === "csc";
	}

	/**
	 * Checks whether the specified document is a script file or GSH macro.
	 * @param document The document being checked
	 * @returns true if so, false otherwise
	 */
	static isScript(document: vscode.TextDocument): boolean {
		return this.isScriptFile(document) || document.languageId === "gsh";
	}
}