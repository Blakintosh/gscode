import { workspace } from "vscode";

/** The payload sent to the server as initializationOptions and on configuration changes. */
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
    "completion.literals": boolean;
    "completion.fieldScope": string;
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
        "completion.literals": config.get<boolean>("completion.literals", true),
        "completion.fieldScope": config.get<string>("completion.fieldScope", "owner"),
    };
}
