import { workspace } from "vscode";

/** The payload sent to the server as initializationOptions and on configuration changes. */
export interface GscodeSettings {
    serverLogLevel: string;
}

/** Reads the current gscode.* settings into the shape the server expects. */
export function readSettings(): GscodeSettings {
    const config = workspace.getConfiguration("gscode");
    return {
        serverLogLevel: config.get<string>("serverLogLevel", "off"),
    };
}
