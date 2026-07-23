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

    // Open gscode.net for whatever is under the cursor: the engine function's own page when it is
    // one, otherwise the library index for the editor's language.
    //
    // Whether a name IS a builtin is not a text question — a script function of the same name
    // shadows it — so the server is asked. The extension host has no symbol knowledge at all.
    context.subscriptions.push(
        vscode.commands.registerCommand("gscode.openApiLibrary", async () => {
            const editor = vscode.window.activeTextEditor;
            const library = editor?.document.languageId === "csc" ? "csc" : "gsc";
            let page = `https://www.gscode.net/library/${library}`;

            if (editor !== undefined) {
                try {
                    const builtin = await created.sendRequest<{ name: string; language: string }>(
                        "gscode/builtinAt",
                        {
                            uri: editor.document.uri.toString(),
                            line: editor.selection.active.line,
                            character: editor.selection.active.character,
                        },
                    );

                    if (builtin?.name) {
                        // The site addresses pages in lowercase: LUINotifyEvent -> luinotifyevent.
                        page = `https://www.gscode.net/library/${builtin.language}/${builtin.name.toLowerCase()}`;
                    }
                } catch (error) {
                    // The index is still a useful answer, so a failed lookup opens that instead of
                    // nothing at all.
                    log.warn(`Could not resolve the symbol under the cursor: ${String(error)}`);
                }
            }

            await vscode.env.openExternal(vscode.Uri.parse(page));
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
    registerSemicolonDeduplication(context);
}

/**
 * Removes the second of two adjacent semicolons, right after one is typed.
 *
 * Completion finishes a statement call with its `;`, and finishing the line with `);` is muscle
 * memory — so the semicolon needs the same "type over it" behaviour the editor already gives the
 * closing parenthesis through `editor.autoClosingOvertype`.
 *
 * This lives client-side rather than in the server's on-type formatting handler, which is where it
 * belongs conceptually, because VSCode only sends `textDocument/onTypeFormatting` when
 * `editor.formatOnType` is enabled — and that is off by default. Turning it on to reach the
 * handler would also run the whole document formatter on every `}` and `;` as the user types,
 * which is a far larger change than swallowing one character.
 */
function registerSemicolonDeduplication(context: vscode.ExtensionContext): void {
    const languages = new Set(["gsc", "csc", "gsh"]);

    // Our own edit fires this event again; without the guard it would recurse.
    let applying = false;

    context.subscriptions.push(
        vscode.workspace.onDidChangeTextDocument(async (event) => {
            if (applying || !languages.has(event.document.languageId) || event.contentChanges.length !== 1) {
                return;
            }

            const change = event.contentChanges[0];
            // A single typed ';' — not a paste, not a replacement.
            if (change.text !== ";" || !change.range.isEmpty) {
                return;
            }

            const after = change.range.start.translate(0, 1);
            const following = new vscode.Range(after, after.translate(0, 1));
            if (event.document.getText(following) !== ";") {
                return;
            }

            // `for ( ;; )` is the language, not a mistake.
            const line = event.document.lineAt(after.line).text;
            if (isInsideForHeader(line, after.character)) {
                return;
            }

            const editor = vscode.window.visibleTextEditors.find((e) => e.document === event.document);
            if (editor === undefined) {
                return;
            }

            applying = true;
            try {
                // Delete the one AHEAD of the cursor rather than the one just typed: the visible
                // result is identical, and the cursor is left after the surviving semicolon
                // instead of before it.
                await editor.edit((builder) => builder.delete(following), {
                    undoStopBefore: false,
                    undoStopAfter: false,
                });
            } finally {
                applying = false;
            }
        }),
    );
}

/** Whether `character` sits inside a `for ( … )` header on this line. */
function isInsideForHeader(line: string, character: number): boolean {
    let depth = 0;

    for (let index = character - 1; index >= 0; index--) {
        const c = line[index];
        if (c === ")") {
            depth++;
        } else if (c === "(") {
            if (depth === 0) {
                return /\bfor\s*$/.test(line.slice(0, index));
            }
            depth--;
        }
    }

    return false;
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
        "gscode/gameMismatch",
        async (params: { selectedGame: string; selectedDisplayName: string; fileLooksLikeBlackOps3: boolean }) => {
            // The games the extension can target, in release order, with their display names.
            const games: { id: string; label: string }[] = [
                { id: "cod4", label: "Call of Duty 4: Modern Warfare (2007)" },
                { id: "waw", label: "Call of Duty: World at War (2008)" },
                { id: "mw2", label: "Call of Duty: Modern Warfare 2 (2009)" },
                { id: "bo1", label: "Call of Duty: Black Ops (2010)" },
                { id: "mw3", label: "Call of Duty: Modern Warfare 3 (2011)" },
                { id: "bo2", label: "Call of Duty: Black Ops II (2012)" },
                { id: "ghosts", label: "Call of Duty: Ghosts (2013)" },
                { id: "aw", label: "Call of Duty: Advanced Warfare (2014)" },
                { id: "bo3", label: "Call of Duty: Black Ops III (2015)" },
            ];

            const looksLike = params.fileLooksLikeBlackOps3
                ? "Black Ops III"
                : "an earlier Call of Duty";
            const choice = await vscode.window.showInformationMessage(
                `This file looks like ${looksLike}, but the game version is set to ${params.selectedDisplayName}. Switch it?`,
                "Choose Game…",
                "Not Now",
            );

            if (choice !== "Choose Game…") {
                return;
            }

            const picked = await vscode.window.showQuickPick(
                games.map((g) => ({ label: g.label, id: g.id, picked: g.id === params.selectedGame })),
                { title: "Select the game this workspace targets", placeHolder: "Call of Duty game" },
            );

            if (picked) {
                await vscode.workspace
                    .getConfiguration("gscode")
                    .update("game", picked.id, vscode.ConfigurationTarget.Workspace);
                log.info(`Game version set to ${picked.id}.`);
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
