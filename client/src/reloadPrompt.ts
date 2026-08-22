import * as vscode from "vscode";

/**
 * Settings the running server cannot pick up, and what each one is stale against.
 *
 * The server reads these exactly once. `game` is worse than the rest: it is a COMMAND LINE
 * argument, because the bundled data (builtin API, engine fields) resolves while the container is
 * built, before the initialize handshake — so a game arriving in the handshake is already too late.
 * The other three build the RootConfig at initialize, and nothing rebuilds it on a settings push;
 * only a workspace-folder change does.
 *
 * Left unprompted, the failure is silent and reads as a bug in the extension: you point rawPath at
 * the right folder, nothing changes, and every #using still reports as missing.
 */
const RESTART_REQUIRED: ReadonlyArray<{ section: string; label: string }> = [
    { section: "gscode.game", label: "game" },
    { section: "gscode.rawPath", label: "raw folder" },
    { section: "gscode.modsPath", label: "mods folder" },
    { section: "gscode.raw.enabled", label: "raw file reading" },
];

/**
 * Set by a command that writes one of the above and prompts about it ITSELF.
 *
 * The game picker is the case: it writes gscode.game and immediately asks to reload, so the generic
 * prompt below would be a second notification saying the same thing about the same edit. One shot
 * rather than a scope, because the write and the change event are one turn apart and anything
 * longer-lived would swallow a real edit made while it was open.
 */
let suppressNext = false;

/** Skips the next restart-required prompt. See {@link suppressNext}. */
export function suppressReloadPromptOnce(): void {
    suppressNext = true;
}

/**
 * Prompts to reload the window when one of the above changes.
 *
 * A window reload rather than `gscode.restartServer`, which is NOT sufficient here: the server's
 * launch arguments and initializationOptions are captured when the LanguageClient is constructed,
 * so a restart relaunches with the settings the session started with. Reloading re-runs activate,
 * which reads them afresh.
 */
export function registerReloadPrompt(
    context: vscode.ExtensionContext,
    log: vscode.LogOutputChannel,
): void {
    // One prompt at a time. VSCode fires a change event per edit, so editing rawPath and then
    // modsPath would otherwise stack two identical notifications, and answering one would leave
    // the other behind.
    let prompting = false;

    context.subscriptions.push(
        vscode.workspace.onDidChangeConfiguration(async (event) => {
            const changed = RESTART_REQUIRED
                .filter((setting) => event.affectsConfiguration(setting.section))
                .map((setting) => setting.label);

            if (changed.length === 0 || prompting) {
                return;
            }

            if (suppressNext) {
                suppressNext = false;
                log.info(`Restart-required setting changed by a command that prompts itself: ${changed.join(", ")}`);
                return;
            }

            log.info(`Restart-required setting changed: ${changed.join(", ")}`);
            prompting = true;
            try {
                const choice = await vscode.window.showInformationMessage(
                    `The GSCode ${changed.join(" and ")} setting${changed.length > 1 ? "s" : ""} changed. `
                    + "Reload the window to apply it.",
                    "Reload Window",
                    "Later",
                );

                if (choice === "Reload Window") {
                    await vscode.commands.executeCommand("workbench.action.reloadWindow");
                }
            } finally {
                prompting = false;
            }
        }),
    );
}
