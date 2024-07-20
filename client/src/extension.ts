/* --------------------------------------------------------------------------------------------
 * Copyright (c) Microsoft Corporation. All rights reserved.
 * Licensed under the MIT License. See License.txt in the project root for license information.
 * ------------------------------------------------------------------------------------------ */
// tslint:disable
"use strict";

import * as path from "path";

import { workspace, Disposable, ExtensionContext, window } from "vscode";
import {
    LanguageClient,
    LanguageClientOptions,
    SettingMonitor,
    ServerOptions,
    TransportKind,
    InitializeParams,
    StreamInfo,
    createServerPipeTransport,
} from "vscode-languageclient/node";
import { Trace, createClientPipeTransport } from "vscode-jsonrpc/node";
import { createConnection } from "net";
import dotenv = require("dotenv");

let client: LanguageClient;

export function activate(context: ExtensionContext) {
    // The server is implemented in node
    let serverExe = "dotnet";

    dotenv.config({ path: path.join(context.extensionPath, ".env") });

	const serverModulePath = path.join('..', 'server', 'GSCode.NET', 'bin', 'Debug', 'net8.0', 'GSCode.NET.dll');
    const serverModule = context.asAbsolutePath(path.normalize(serverModulePath));

    console.log(serverModule);

	const serverLocation = process.env.LSP_LOCATION;
	if (!serverLocation) {
		throw new Error("LSP_LOCATION environment variable is not set. Please set it to the location of the GSCode.NET Language Server in .env");
	}

    // If the extension is launched in debug mode then the debug server options are used
    // Otherwise the run options are used
    let serverOptions: ServerOptions = {
        // run: { command: serverExe, args: ['-lsp', '-d'] },
        run: {
            command: serverExe,
			args: [serverModule],
            // args: [serverLocation],
            // args: [path.join(serverLocation, 'GSCode.NET.dll')],
        },
        // debug: { command: serverExe, args: ['-lsp', '-d'] }
        debug: {
            command: serverExe,
			args: [serverModule],
            // args: [path.join(serverLocation, 'GSCode.NET.exe')],
        },
    };

    const watcher = workspace.createFileSystemWatcher("**/*.gsc");

    // Options to control the language client
    let clientOptions: LanguageClientOptions = {
        // Register the server for plain text documents
        documentSelector: [
            {
                pattern: "**/*.gsc",
            },
        ],
        progressOnInitialization: true,
        synchronize: {
            // Synchronize the setting section 'languageServerExample' to the server
            configurationSection: "gsc",
            fileEvents: watcher,
        }, 
    };

    watcher.onDidChange((e) => {
        console.log(`File changed: ${e.fsPath}`);
    });

    // Create the language client and start the client.
    client = new LanguageClient("gsc", "GSCode.NET Language Server", serverOptions, clientOptions);

    // Push the disposable to the context's subscriptions so that the
    // client can be deactivated on extension deactivation
    client.start();
}


export function deactivate(): Thenable<void> | undefined {
	if (!client) {
		return undefined;
	}
	return client.stop();
}