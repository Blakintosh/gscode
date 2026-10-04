import * as vscode from "vscode";
import { LanguageClient } from "vscode-languageclient/node";
import { suppressReloadPromptOnce } from "./reloadPrompt";

/** One game the server says may be picked. */
interface SupportedGame {
    id: string;
    label: string;
}

/** The gscode/supportedGames response: the roster, and which profile is actually running. */
interface SupportedGamesResponse {
    selectedGame: string;
    selectedDisplayName: string;
    games: SupportedGame[];
}

/**
 * Shows the game picker and applies the choice.
 *
 * The roster is ASKED FOR every time rather than kept here. That is not caution about staleness —
 * it is the bug this file exists to not repeat. The client used to hold its own list, it drifted to
 * nine games with four of them cores that have no dialect implemented, and picking one of those
 * wrote a `gscode.game` value the setting's own enum rejects, which the server then silently
 * resolved back to Black Ops III. The server is the only side that knows which profiles are
 * supported, so a request that fails is reported rather than falling back to a list — a fallback
 * list IS the failure mode.
 *
 * The tick goes on what the SERVER selected, not on what `gscode.game` says. The two differ exactly
 * when something is wrong: an unrecognised name falls back to BO3, so the setting reads as what was
 * asked for while the server runs something else. A picker that ticks a game which is not in use
 * rules out the very thing the user is trying to find.
 *
 * @returns the id that was written, or undefined when nothing changed.
 */
export async function pickGame(
    client: LanguageClient,
    log: vscode.LogOutputChannel,
    options: { title: string },
): Promise<string | undefined> {
    let roster: SupportedGamesResponse;
    try {
        roster = await client.sendRequest<SupportedGamesResponse>("gscode/supportedGames", {});
    } catch (error) {
        log.error(`Could not ask the server which games it supports: ${String(error)}`);
        await vscode.window.showErrorMessage(
            "GSCode could not reach the language server to list the available games.",
        );
        return undefined;
    }

    return applyRoster(roster.games ?? [], roster.selectedGame, log, options.title);
}

/**
 * The half of the picker that works from a roster already in hand.
 *
 * gscode/gameMismatch carries the roster with it, so the offer to switch after a mismatch has
 * everything it needs and must not make a second round trip to ask the same question again.
 */
export async function pickGameFrom(
    games: SupportedGame[],
    selectedGame: string,
    log: vscode.LogOutputChannel,
    title: string,
): Promise<string | undefined> {
    return applyRoster(games, selectedGame, log, title);
}

async function applyRoster(
    games: SupportedGame[],
    selectedGame: string,
    log: vscode.LogOutputChannel,
    title: string,
): Promise<string | undefined> {
    if (games.length === 0) {
        log.warn("The server offered no supported games; not showing a picker.");
        return undefined;
    }

    // description rather than `picked`, which does nothing in a single-select QuickPick — it is a
    // multi-select affordance, and reads as a no-op checkmark that never appears.
    const items = games.map((game) => ({
        label: game.label,
        description: game.id === selectedGame ? "$(check) current" : undefined,
        id: game.id,
    }));

    const chosen = await vscode.window.showQuickPick(items, {
        title,
        placeHolder: "Call of Duty game",
    });

    if (chosen === undefined || chosen.id === selectedGame) {
        return undefined;
    }

    // Workspace scope when there is one, because a workspace targets a game — a mod is for one
    // title. Falling back to Global is not a preference: `update` on the workspace target throws
    // when no folder is open, so an untitled window would fail rather than pick anything.
    const target = vscode.workspace.workspaceFolders?.length
        ? vscode.ConfigurationTarget.Workspace
        : vscode.ConfigurationTarget.Global;

    // The reload prompt watches gscode.game and would fire on this write. It is the right prompt
    // for someone editing the setting by hand and a duplicate here, since asking is the next thing
    // this function does.
    suppressReloadPromptOnce();
    await vscode.workspace.getConfiguration("gscode").update("game", chosen.id, target);
    log.info(`Game set to ${chosen.id} (${vscode.ConfigurationTarget[target]}).`);

    // A window reload, not gscode.restartServer. The game is a COMMAND LINE argument — the bundled
    // data resolves while the container is built, before the initialize handshake — and the launch
    // arguments are captured when the LanguageClient is constructed. A restart therefore relaunches
    // with the game this session started with, and the setting would appear to do nothing.
    const choice = await vscode.window.showInformationMessage(
        `GSCode is now set to ${chosen.label}. Reload the window to apply it.`,
        "Reload Window",
        "Later",
    );

    if (choice === "Reload Window") {
        await vscode.commands.executeCommand("workbench.action.reloadWindow");
    }

    return chosen.id;
}
