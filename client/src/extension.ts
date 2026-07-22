// No fs/os/path: the extension host does no filesystem work of its own. Everything path-shaped
// belongs to the server, which is the side that knows the roots, the cache layout and the
// resolution rules.
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

    // Clear the persistent cache and re-index.
    //
    // The server does the deleting, because it is the only side that knows WHICH cache is ours:
    // caches are per-workspace, named <hash>.db under a shared directory. Doing it here meant
    // recursively deleting that whole directory and throwing away every other workspace's cache
    // to reindex this one — and rebuilding the path from process.env.APPDATA, whose `??` fallback
    // does not trigger on an empty string, so an empty APPDATA aimed a recursive force delete at
    // a relative path resolved against the extension host's working directory.
    //
    // It also drains the SQLite writer properly rather than stopping the server and sleeping 300ms.
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
                const response = await created.sendRequest<{ deleted: boolean; message: string }>(
                    "gscode/clearCache",
                    {},
                );
                if (!response.deleted && response.message) {
                    log.info(`Cache not deleted: ${response.message}`);
                }
            } catch (error) {
                // Reload anyway: a reindex without a cleared cache is still closer to what was
                // asked for than doing nothing.
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

/**
 * The server's status: a spinner counting files while indexing, then a ready state whose tooltip
 * carries what was indexed, how long it took and what the server is holding.
 */
function registerIndexingStatusBar(
    context: vscode.ExtensionContext,
    languageClient: LanguageClient,
    log: vscode.LogOutputChannel,
): void {
    const statusBar = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 0);
    statusBar.name = "GSCode Status";
    statusBar.command = "gscode.showOutput";
    context.subscriptions.push(statusBar);

    const formatCount = (value: number) => value.toLocaleString();

    // The ready tooltip is assembled from two independent sources — the indexing summary, which
    // arrives once, and memory, which keeps changing — so it is held here and re-rendered
    // whenever either moves. Building it inside a single notification handler is what left the
    // memory figure frozen at whatever it was the instant indexing finished.
    let indexSummary: { files: number; seconds: string } | undefined;
    let memoryMegabytes: number | undefined;

    const renderTooltip = () => {
        if (indexSummary === undefined) {
            return;
        }

        const lines = [
            "**GSCode** — ready",
            "",
            `Indexed **${formatCount(indexSummary.files)}** files in **${indexSummary.seconds}s**`,
        ];

        if (memoryMegabytes !== undefined) {
            lines.push("");
            lines.push(`Server memory **${memoryMegabytes.toFixed(0)} MB**`);
        }

        lines.push("", "_Click to open the server log._");
        statusBar.tooltip = new vscode.MarkdownString(lines.join("\n"));
    };

    languageClient.onNotification("gscode/serverStatus", (params: { workingSetMegabytes: number }) => {
        memoryMegabytes = params.workingSetMegabytes;
        renderTooltip();
    });

    languageClient.onNotification("gscode/indexingStarted", (params: { totalFiles: number }) => {
        statusBar.text = `$(sync~spin) GSCode: indexing 0/${formatCount(params.totalFiles)}`;
        statusBar.tooltip = `Indexing ${formatCount(params.totalFiles)} script files…`;
        statusBar.show();
        // Not logged here: the server writes this line to its own channel now. Writing it from
        // the extension host put the one message announcing indexing in a different output
        // channel from everything else the language server says.
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
        (params: {
            filesIndexed: number;
            totalFiles: number;
            elapsedMilliseconds: number;
            workingSetMegabytes: number;
        }) => {
            statusBar.text = "$(check) GSCode: ready";

            indexSummary = {
                files: params.filesIndexed,
                seconds: (params.elapsedMilliseconds / 1000).toFixed(1),
            };
            // A starting value, so the tooltip is complete before the first status push. It is
            // then kept current by gscode/serverStatus rather than staying at this sample.
            memoryMegabytes = params.workingSetMegabytes;

            renderTooltip();
            // The server logs its own completion line; repeating it here would double it.
        },
    );
}

export function deactivate(): Thenable<void> | undefined {
    return client?.stop();
}
