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

    // Open the gscode.net script API library for the active editor's language (default gsc).
    context.subscriptions.push(
        vscode.commands.registerCommand("gscode.openApiLibrary", () => {
            const languageId = vscode.window.activeTextEditor?.document.languageId;
            const library = languageId === "csc" ? "csc" : "gsc";
            vscode.env.openExternal(vscode.Uri.parse(`https://www.gscode.net/library/${library}`));
        }),
    );

    // Bridge for code-lens "N references" clicks: the server sends plain JSON args, which
    // VSCode's editor.action.showReferences rejects via instanceof checks, so we re-fetch
    // references through the provider and hand it real Location instances.
    context.subscriptions.push(
        vscode.commands.registerCommand(
            "gscode.showReferences",
            async (uriString: string, position: { line: number; character: number }) => {
                const uri = vscode.Uri.parse(uriString);
                const pos = new vscode.Position(position.line, position.character);
                const locations = await vscode.commands.executeCommand<vscode.Location[]>(
                    "vscode.executeReferenceProvider", uri, pos) ?? [];
                await vscode.commands.executeCommand("editor.action.showReferences", uri, pos, locations);
            },
        ),
    );

    registerIndexingStatusBar(context, created);

    await created.start();
    log.info("GSCode language client started");
}

/** The live indexing counter: a spinner whose number races upward as files complete. */
function registerIndexingStatusBar(context: vscode.ExtensionContext, languageClient: LanguageClient): void {
    const statusBar = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 0);
    statusBar.name = "GSCode Indexing";
    statusBar.command = "gscode.showOutput";
    context.subscriptions.push(statusBar);

    const formatCount = (value: number) => value.toLocaleString();

    languageClient.onNotification("gscode/indexingStarted", (params: { totalFiles: number }) => {
        statusBar.text = `$(sync~spin) GSCode: indexing 0/${formatCount(params.totalFiles)}`;
        statusBar.tooltip = `Indexing ${formatCount(params.totalFiles)} script files…`;
        statusBar.show();
    });

    languageClient.onNotification("gscode/indexingProgress", (params: { filesIndexed: number; totalFiles: number }) => {
        statusBar.text = `$(sync~spin) GSCode: indexing ${formatCount(params.filesIndexed)}/${formatCount(params.totalFiles)}`;
    });

    languageClient.onNotification(
        "gscode/indexingComplete",
        (params: { filesIndexed: number; totalFiles: number; elapsedMilliseconds: number }) => {
            const seconds = (params.elapsedMilliseconds / 1000).toFixed(1);
            statusBar.text = "$(check) GSCode: ready";
            statusBar.tooltip = `Indexed ${formatCount(params.filesIndexed)} files in ${seconds}s`;
        },
    );
}

export function deactivate(): Thenable<void> | undefined {
    return client?.stop();
}
