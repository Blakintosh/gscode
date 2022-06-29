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

import { ScriptDiagnostic } from "../../diagnostics/ScriptDiagnostic";
import { ScriptReader } from "../../logic/ScriptReader";
import * as vscode from "vscode";
import { ScriptSemanticToken } from "../../ScriptSemanticToken";

export abstract class StatementContents {
    diagnostics: ScriptDiagnostic[] = [];
    semantics: ScriptSemanticToken[] = [];
    /**
     * Parses the given statement contents, which may include recursive calls.
     */
    abstract parse(reader: ScriptReader): void;

    pushDiagnostic(location: [number, number], message: string, severity: vscode.DiagnosticSeverity | undefined = undefined) {
        this.diagnostics.push(new ScriptDiagnostic(location, message, severity));
    }

    pushSemantic(location: [number, number], tokenType: string, tokenModifiers: string[] | undefined = undefined): void {
        this.semantics.push(new ScriptSemanticToken(location, tokenType, tokenModifiers));
    }
}