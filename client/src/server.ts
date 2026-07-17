import { execFile } from "child_process";
import * as path from "path";
import * as vscode from "vscode";
import {
    LanguageClient,
    LanguageClientOptions,
    ServerOptions,
    TransportKind,
} from "vscode-languageclient/node";
import * as dotenv from "dotenv";
import { readSettings } from "./settings";

const REQUIRED_DOTNET_MAJOR = 10;
const DOTNET_DOWNLOAD_URL = `https://dotnet.microsoft.com/download/dotnet/${REQUIRED_DOTNET_MAJOR}.0`;

/** Checks `dotnet --list-runtimes` for the required Microsoft.NETCore.App major version. */
function isDotnetRuntimeAvailable(): Promise<boolean> {
    return new Promise((resolve) => {
        execFile("dotnet", ["--list-runtimes"], { encoding: "utf-8" }, (error, stdout) => {
            if (error) {
                resolve(false);
                return;
            }
            const pattern = new RegExp(`Microsoft\\.NETCore\\.App ${REQUIRED_DOTNET_MAJOR}\\.`);
            resolve(pattern.test(stdout));
        });
    });
}

/**
 * Resolves the folder holding GSCode.Server.dll. Packaged builds use the bundled
 * "service" folder; debug sessions read the location from client/.env.
 */
function resolveServerFolder(context: vscode.ExtensionContext): string {
    dotenv.config({ path: path.join(context.extensionPath, ".env") });

    if (process.env.VSCODE_DEBUG) {
        const debugLocation = process.env.SHOULD_TEST_IN_RELEASE === "true"
            ? process.env.SERVER_LOCATION
            : process.env.DEBUG_SERVER_LOCATION;
        if (!debugLocation) {
            throw new Error("DEBUG_SERVER_LOCATION is not set in client/.env — point it at the GSCode.Server build output.");
        }
        return debugLocation;
    }

    return "service";
}

/**
 * Verifies the runtime, then builds the LanguageClient that spawns
 * `dotnet GSCode.Server.dll` over a named pipe. Returns undefined when the
 * runtime is missing (after prompting the user to install it).
 */
export async function createLanguageClient(
    context: vscode.ExtensionContext,
    log: vscode.LogOutputChannel,
): Promise<LanguageClient | undefined> {
    if (!await isDotnetRuntimeAvailable()) {
        log.error(`.NET ${REQUIRED_DOTNET_MAJOR} runtime not found`);
        const selection = await vscode.window.showErrorMessage(
            `GSCode requires the .NET ${REQUIRED_DOTNET_MAJOR} runtime. Please install it and reload the window.`,
            "Download .NET",
            "Dismiss",
        );
        if (selection === "Download .NET") {
            vscode.env.openExternal(vscode.Uri.parse(DOTNET_DOWNLOAD_URL));
        }
        return undefined;
    }

    const serverDll = context.asAbsolutePath(
        path.normalize(path.join(resolveServerFolder(context), "GSCode.Server.dll")),
    );
    log.info(`Launching server: dotnet ${serverDll}`);

    const serverOptions: ServerOptions = {
        run: { command: "dotnet", transport: TransportKind.pipe, args: [serverDll] },
        debug: { command: "dotnet", transport: TransportKind.pipe, args: [serverDll] },
    };

    // Server stderr (Serilog) lands in this channel; the "GSCode" LogOutputChannel
    // stays reserved for extension-host lifecycle messages.
    const serverChannel = vscode.window.createOutputChannel("GSCode Server");
    context.subscriptions.push(serverChannel);

    const clientOptions: LanguageClientOptions = {
        documentSelector: [
            { scheme: "file", language: "gsc", pattern: "**/*.gsc" },
            { scheme: "file", language: "csc", pattern: "**/*.csc" },
            { scheme: "file", language: "gsh", pattern: "**/*.gsh" },
        ],
        synchronize: {
            configurationSection: "gscode",
        },
        initializationOptions: {
            gscode: readSettings(),
        },
        outputChannel: serverChannel,
    };

    return new LanguageClient("gscode", "GSCode Language Server", serverOptions, clientOptions);
}
