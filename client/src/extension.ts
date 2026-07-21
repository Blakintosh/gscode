import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import * as vscode from "vscode";
import { LanguageClient } from "vscode-languageclient/node";
import { createLanguageClient } from "./server";

let client: LanguageClient | undefined;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
    // Extension-host lifecycle log. Respects VSCode's per-channel log level
    // ("Developer: Set Log Level…"); server-side events never land here.
    const log = vscode.window.createOutputChannel("GSCode", { log: true });
    context.subscriptions.push(log);
    log.info("GSCode activating");

    const created = await createLanguageClient(context, log);
    if (!created) {
        return;
    }
    client = created;

    context.subscriptions.push(
        vscode.commands.registerCommand("gscode.showOutput", () => {
            created.outputChannel.show();
        }),
    );

    // Restart the language server (e.g. after changing an environment variable it reads).
    context.subscriptions.push(
        vscode.commands.registerCommand("gscode.restartServer", async () => {
            log.info("Restarting GSCode language server");
            await created.restart();
        }),
    );

    // Clear the persistent cache and re-index: stop the server (releasing the SQLite lock),
    // delete the cache directory, then reload the window for a fresh cold index.
    context.subscriptions.push(
        vscode.commands.registerCommand("gscode.clearCacheAndReindex", async () => {
            const choice = await vscode.window.showWarningMessage(
                "Clear the GSCode cache and re-index? The language server will restart.",
                { modal: true },
                "Clear and Reindex",
            );
            if (choice !== "Clear and Reindex") {
                return;
            }

            log.info("Clearing cache and reindexing");
            try {
                await created.stop();
                // Give the server a moment to release the SQLite file handles.
                await new Promise((resolve) => setTimeout(resolve, 300));
                // Mirrors the server's cache location (Environment.SpecialFolder.ApplicationData:
                // %APPDATA% on Windows, ~/.config elsewhere).
                const appData = process.env.APPDATA ?? path.join(os.homedir(), ".config");
                const cacheDir = path.join(appData, "gscode", "cache");
                await fs.promises.rm(cacheDir, { recursive: true, force: true });
            } catch (error) {
                log.error(`Failed to clear cache: ${String(error)}`);
            }
            await vscode.commands.executeCommand("workbench.action.reloadWindow");
        }),
    );

    // Open the gscode.net script API library for the active editor's language (default gsc).
    context.subscriptions.push(
        vscode.commands.registerCommand("gscode.openApiLibrary", () => {
            const languageId = vscode.window.activeTextEditor?.document.languageId;
            const library = languageId === "csc" ? "csc" : "gsc";
            vscode.env.openExternal(vscode.Uri.parse(`https://www.gscode.net/library/${library}`));
        }),
    );

    // Bridge for code-lens "N references" clicks. Two things are going on:
    //
    //  1. editor.action.showReferences validates its arguments with instanceof, so plain JSON
    //     will not do — we re-fetch through the provider to get real Location instances.
    //  2. The position arrives as two NUMBERS, not an object. An object round-tripped through
    //     the server's JArray kept its C# PascalCase ("Line"/"Character"), so reading
    //     position.line gave undefined and the Position constructor threw "Unexpected type".
    context.subscriptions.push(
        vscode.commands.registerCommand(
            "gscode.showReferences",
            async (uriString: string, line: number, character: number) => {
                if (typeof line !== "number" || typeof character !== "number") {
                    log.error(`showReferences got a bad position: ${JSON.stringify([line, character])}`);
                    return;
                }

                const uri = vscode.Uri.parse(uriString);
                const pos = new vscode.Position(line, character);
                const locations = await vscode.commands.executeCommand<vscode.Location[]>(
                    "vscode.executeReferenceProvider", uri, pos) ?? [];
                await vscode.commands.executeCommand("editor.action.showReferences", uri, pos, locations);
            },
        ),
    );

    registerIndexingStatusBar(context, created, log);

    await created.start();
    log.info("GSCode language client started");

    registerRenameDirectiveFixup(context, created, log);
}

/**
 * Keeps `#using`/`#insert` paths correct when a script is renamed or moved.
 *
 * This lives client-side because OmniSharp 0.19.9 models the LSP `FileRename` with a single
 * `Uri` — the spec's `oldUri`/`newUri` pair is missing, so a server-side `willRenameFiles`
 * handler cannot see where a file is going. VSCode's own event has both, so the client sources
 * the event and asks the server (which owns the dependency data) to plan the edits.
 */
function registerRenameDirectiveFixup(
    context: vscode.ExtensionContext,
    languageClient: LanguageClient,
    log: vscode.LogOutputChannel,
): void {
    interface PlanRenameEdit {
        path: string;
        startLine: number;
        startCharacter: number;
        endLine: number;
        endCharacter: number;
        newText: string;
    }

    context.subscriptions.push(
        vscode.workspace.onWillRenameFiles((event) => {
            const scripts = event.files.filter((file) => /\.(gsc|csc|gsh)$/i.test(file.oldUri.fsPath));
            if (scripts.length === 0) {
                return;
            }

            // waitUntil defers the rename until the edit resolves, so both apply together.
            event.waitUntil(
                (async () => {
                    const edit = new vscode.WorkspaceEdit();
                    let total = 0;

                    for (const file of scripts) {
                        const response = await languageClient.sendRequest<{ edits: PlanRenameEdit[] }>(
                            "gscode/planRename",
                            { oldPath: file.oldUri.fsPath, newPath: file.newUri.fsPath },
                        );

                        for (const planned of response?.edits ?? []) {
                            edit.replace(
                                vscode.Uri.file(planned.path),
                                new vscode.Range(
                                    planned.startLine,
                                    planned.startCharacter,
                                    planned.endLine,
                                    planned.endCharacter,
                                ),
                                planned.newText,
                            );
                            total++;
                        }
                    }

                    if (total > 0) {
                        log.info(`Rename: updating ${total} directive path(s) across the workspace`);
                    }

                    return edit;
                })(),
            );
        }),
    );
}

/** The live indexing counter: a spinner whose number races upward as files complete. */
function registerIndexingStatusBar(
    context: vscode.ExtensionContext,
    languageClient: LanguageClient,
    log: vscode.LogOutputChannel,
): void {
    const statusBar = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 0);
    statusBar.name = "GSCode Indexing";
    statusBar.command = "gscode.showOutput";
    context.subscriptions.push(statusBar);

    const formatCount = (value: number) => value.toLocaleString();

    languageClient.onNotification("gscode/indexingStarted", (params: { totalFiles: number }) => {
        statusBar.text = `$(sync~spin) GSCode: indexing 0/${formatCount(params.totalFiles)}`;
        statusBar.tooltip = `Indexing ${formatCount(params.totalFiles)} script files…`;
        statusBar.show();
        log.info(`Indexing ${formatCount(params.totalFiles)} script files…`);
    });

    languageClient.onNotification("gscode/indexingProgress", (params: { filesIndexed: number; totalFiles: number }) => {
        statusBar.text = `$(sync~spin) GSCode: indexing ${formatCount(params.filesIndexed)}/${formatCount(params.totalFiles)}`;
    });

    // Warn once per file per session: the save already happened, so repeating the toast on
    // every later save of the same file would nag rather than inform.
    const warnedRawFiles = new Set<string>();

    languageClient.onNotification(
        "gscode/rawFolderWriteWarning",
        async (params: { path: string; relativePath: string; isStockScript: boolean }) => {
            if (warnedRawFiles.has(params.path)) {
                return;
            }

            warnedRawFiles.add(params.path);

            const what = params.isStockScript ? "a stock script" : "a file in the game's raw folder";
            const detail = params.relativePath || params.path;
            log.warn(`Saved ${what}: ${detail}`);

            const choice = await vscode.window.showWarningMessage(
                `You just saved ${what} (${detail}). Mod tools updates overwrite raw, so edits here can be lost — consider copying the file into your mod folder instead.`,
                "Don't Warn Again",
            );

            if (choice === "Don't Warn Again") {
                await vscode.workspace
                    .getConfiguration("gscode")
                    .update("rawFileWarningMode", "off", vscode.ConfigurationTarget.Global);
                log.info("Raw folder write warnings disabled (gscode.rawFileWarningMode = off).");
            }
        },
    );

    languageClient.onNotification(
        "gscode/indexingComplete",
        (params: { filesIndexed: number; totalFiles: number; elapsedMilliseconds: number }) => {
            const seconds = (params.elapsedMilliseconds / 1000).toFixed(1);
            statusBar.text = "$(check) GSCode: ready";
            statusBar.tooltip = `Indexed ${formatCount(params.filesIndexed)} files in ${seconds}s`;
            log.info(`Workspace indexing complete: ${formatCount(params.filesIndexed)} files in ${seconds}s`);
        },
    );
}

export function deactivate(): Thenable<void> | undefined {
    return client?.stop();
}
