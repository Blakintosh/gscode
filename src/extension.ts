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

let client: LanguageClient;

export function activate(context: ExtensionContext) {
    // The server is implemented in node
    let serverExe = "dotnet";

	const serverModule = context.asAbsolutePath(
		path.join('resources', 'GSCode.NET.dll')
	);

    // If the extension is launched in debug mode then the debug server options are used
    // Otherwise the run options are used
    let serverOptions: ServerOptions = {
        // run: { command: serverExe, args: ['-lsp', '-d'] },
        run: {
            command: serverExe,
			//args: [serverModule],
            args: ["C:/Users/Blak/source/repos/GSCode.NET/GSCode.NET/bin/Debug/net7.0/GSCode.NET.dll"],
        },
        // debug: { command: serverExe, args: ['-lsp', '-d'] }
        debug: {
            command: serverExe,
			//args: [serverModule],
            args: ["C:/Users/Blak/source/repos/GSCode.NET/GSCode.NET/bin/Debug/net7.0/GSCode.NET.dll"],
        },
    };

    // Options to control the language client
    let clientOptions: LanguageClientOptions = {
        // Register the server for plain text documents
        documentSelector: [
            {
                pattern: "**/*.{gsc,csc}",
            },
        ],
        progressOnInitialization: true,
        synchronize: {
            // Synchronize the setting section 'languageServerExample' to the server
            configurationSection: "gsc",
            fileEvents: workspace.createFileSystemWatcher("**/*.{gsc,csc}"),
        },
    };

    // Create the language client and start the client.
    client = new LanguageClient("gsc", "GSCode.NET Language Server", serverOptions, clientOptions);

    let x = { c: false };
    let a = { b: x };

    // if(a.b.c) {
    //     a.b.c = false;
    // }

    if(a.b.c) {
        console.log("a.b.c is true");
        return false;
    }

    
    console.log(a.b);

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