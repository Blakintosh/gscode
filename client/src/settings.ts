import { workspace } from "vscode";

/**
 * The payload sent to the server as initializationOptions and on configuration changes.
 *
 * An explicit list, so a setting declared in package.json but not named here NEVER REACHES THE
 * SERVER — the server falls back to its own default and the user's choice is silently ignored.
 * Anything added to `contributes.configuration` has to be added here too.
 */
export interface GscodeSettings {
    serverLogLevel: string;
    workspaceIndexingMode: string;
    enableWorkspaceCache: boolean;
    "raw.enabled": boolean;
    rawPath: string;
    modsPath: string;
    rawFileWarningMode: string;
    "outline.showAssignments": boolean;
    "codeLens.enabled": boolean;
    "inlayHints.parameterNames": boolean;
    "inlayHints.inferredTypes": boolean;
    "inlayHints.macroParameterNames": boolean;
    "completion.literals": boolean;
    "completion.fieldScope": string;
    "completion.callPunctuation": string;
    "completion.parameterHints": boolean;
    "diagnostics.scope": string;
    "format.padParens": boolean;
    "format.padCallParens": boolean;
    "format.padBrackets": boolean;
    "format.spaceBeforeControlParen": boolean;
    "format.maxBlankLines": number;
    "format.sortDirectives": boolean;
    "format.alignConsecutive": boolean;
    "game": string;
}

/** Reads the current gscode.* settings into the shape the server expects. */
export function readSettings(): GscodeSettings {
    const config = workspace.getConfiguration("gscode");
    return {
        serverLogLevel: config.get<string>("serverLogLevel", "off"),
        workspaceIndexingMode: config.get<string>("workspaceIndexingMode", "partial"),
        enableWorkspaceCache: config.get<boolean>("enableWorkspaceCache", true),
        "raw.enabled": config.get<boolean>("raw.enabled", true),
        rawPath: config.get<string>("rawPath", ""),
        modsPath: config.get<string>("modsPath", ""),
        rawFileWarningMode: config.get<string>("rawFileWarningMode", "stock"),
        "outline.showAssignments": config.get<boolean>("outline.showAssignments", true),
        "codeLens.enabled": config.get<boolean>("codeLens.enabled", true),
        "inlayHints.parameterNames": config.get<boolean>("inlayHints.parameterNames", true),
        "inlayHints.inferredTypes": config.get<boolean>("inlayHints.inferredTypes", true),
        "inlayHints.macroParameterNames": config.get<boolean>("inlayHints.macroParameterNames", false),
        "completion.literals": config.get<boolean>("completion.literals", true),
        "completion.fieldScope": config.get<string>("completion.fieldScope", "owner"),
        "completion.callPunctuation": config.get<string>("completion.callPunctuation", "parensAndSemicolon"),
        "completion.parameterHints": config.get<boolean>("completion.parameterHints", true),
        "diagnostics.scope": config.get<string>("diagnostics.scope", "workspace"),
        "format.padParens": config.get<boolean>("format.padParens", true),
        "format.padCallParens": config.get<boolean>("format.padCallParens", true),
        "format.padBrackets": config.get<boolean>("format.padBrackets", true),
        "format.spaceBeforeControlParen": config.get<boolean>("format.spaceBeforeControlParen", true),
        "format.maxBlankLines": config.get<number>("format.maxBlankLines", 2),
        "format.sortDirectives": config.get<boolean>("format.sortDirectives", true),
        "format.alignConsecutive": config.get<boolean>("format.alignConsecutive", true),
        "game": config.get<string>("game", "bo3"),
    };
}
