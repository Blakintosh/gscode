import * as fs from "fs";
import * as vscode from "vscode";

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
            let workspacePath = wsFolder.uri.path;

            // If we're not at the base folder, navigate to it
            const base = "/scripts/";
            if(workspacePath.lastIndexOf(base) !== -1) {
                workspacePath = workspacePath.substring(workspacePath.lastIndexOf(base) + base.length);
            }

            // Now see if this script is here
            if(fs.existsSync(workspacePath + "/" + path)) {
                return true;
            }
        } else {
            // We don't want to perform validation if the user hasn't opened a workspace
            return true;
        }
        
        // File not found in workspace, fallback to shared path
        const sharePath = (overrideShared !== "" ? overrideShared : process.env.TA_TOOLS_PATH + "/share/raw/scripts/");

        // Check if it exists in shared
        if(fs.existsSync(sharePath + path)) {
            return true;
        }

        // Not found
        return false;
    }
}