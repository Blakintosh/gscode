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

    await created.start();
    log.info("GSCode language client started");
}

export function deactivate(): Thenable<void> | undefined {
    return client?.stop();
}
