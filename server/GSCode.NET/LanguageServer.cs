﻿using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using GSCode.NET.LSP;
using GSCode.NET.LSP.Utility;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using Serilog;
using StreamJsonRpc;

namespace GSCode.NET;

public class LanguageServer : INotifyPropertyChanged
{
    public JsonRpc Rpc { get; }
    private readonly ScriptManager _scriptManager;

    public event PropertyChangedEventHandler? PropertyChanged;

    public LanguageServer(Stream inputStream, Stream outputStream)
    {
        Rpc = JsonRpc.Attach(outputStream, inputStream, this);
        Rpc.Disconnected += (s, e) => Environment.Exit(0);

        _scriptManager = new();
    }

    [JsonRpcMethod(Methods.InitializeName)]
    public Task<InitializeResult> InitializeAsync(JToken arg)
    {
        Log.Information("Initializing");

        Log.Information("TokenTypes: {0}", SemanticTokensBuilder.SemanticTokenTypes);
        Log.Information("SemanticTokenModifiers: {0}", SemanticTokensBuilder.SemanticTokenModifiers);
        return Task.FromResult(new InitializeResult
        {
            Capabilities = new ServerCapabilities
            {
                TextDocumentSync = new TextDocumentSyncOptions
                {
                    OpenClose = true,
                    Change = TextDocumentSyncKind.Full
                },
                SemanticTokensOptions = new CorrectedSemanticTokensOptions
                {
                    Full = true,
                    Legend = new CorrectedSemanticTokensLegend
                    {
                        TokenTypes = SemanticTokensBuilder.SemanticTokenTypes,
                        TokenModifiers = SemanticTokensBuilder.SemanticTokenModifiers
                    }
                },
                HoverProvider = true,
                //DocumentSymbolProvider = true,
            },
        });
    }

    [JsonRpcMethod(Methods.TextDocumentDidOpenName)]
    public async Task HandleDidOpenTextDocumentNotificationAsync(JToken arg)
    {
        DidOpenTextDocumentParams openParams = arg.ToObject<DidOpenTextDocumentParams>()!;

        Log.Information("Document Opened");
        if (!IsScriptFile(openParams.TextDocument.Uri))
        {
            return;
        }

        // Parse the script
        List<Diagnostic> results = await _scriptManager.AddEditorAsync(openParams.TextDocument);

        _ = PublishDiagnosticsAsync(new PublishDiagnosticParams
        {
            Uri = openParams.TextDocument.Uri,
            Diagnostics = results.ToArray()
        });

        // Your implementation here.
    }

    [JsonRpcMethod(Methods.TextDocumentDidChangeName)]
    public async Task HandleDidChangeTextDocumentNotificationAsync(JToken arg)
    {
        DidChangeTextDocumentParams changeParams = arg.ToObject<DidChangeTextDocumentParams>()!;

        if (!IsScriptFile(changeParams.TextDocument.Uri))
        {
            return;
        }

        List<Diagnostic> results = await _scriptManager.UpdateEditorAsync(changeParams.TextDocument, changeParams.ContentChanges);

        _ = PublishDiagnosticsAsync(new PublishDiagnosticParams
        {
            Uri = changeParams.TextDocument.Uri,
            Diagnostics = results.ToArray()
        });
    }

    [JsonRpcMethod(Methods.TextDocumentDidCloseName)]
    public async Task HandleDidCloseTextDocumentNotificationAsync(JToken arg)
    {
        DidCloseTextDocumentParams closeParams = arg.ToObject<DidCloseTextDocumentParams>()!;

        if (!IsScriptFile(closeParams.TextDocument.Uri))
        {
            return;
        }

        _scriptManager.RemoveEditor(closeParams.TextDocument);

        // Your implementation here.
        _ = PublishDiagnosticsAsync(new PublishDiagnosticParams
        {
            Uri = closeParams.TextDocument.Uri,
            Diagnostics = Array.Empty<Diagnostic>()
        });
    }

    [JsonRpcMethod(Methods.TextDocumentSemanticTokensFullName)]
    public async Task<SemanticTokens> GetSemanticTokensAsync(JToken arg)
    {
        Log.Information("Semantic request");
        SemanticTokensParams semanticTokensParams = arg.ToObject<SemanticTokensParams>()!;

        SemanticTokensBuilder tokens = new();

        WatchedEditor? script = _scriptManager.GetParsedEditor(semanticTokensParams.TextDocument);

        if (script is not null)
        {
            await script.PushSemanticTokensAsync(tokens);
        }

        int[] result = tokens.Encode();

        Log.Information("Result: {0}", result);

        //tokens
        // Your implementation here.
        return new()
        {
            Data = result,
        };
    }

    [JsonRpcMethod(Methods.TextDocumentHoverName)]
    public async Task<Hover?> GetHoverInformationAsync(JToken arg)
    {
        // Your implementation here.
        Log.Information("Hover request");
        TextDocumentPositionParams hoverParams = arg.ToObject<TextDocumentPositionParams>()!;

        Hover? result = null;
        WatchedEditor? script = _scriptManager.GetParsedEditor(hoverParams.TextDocument);

        if (script is not null)
        {
            result = await script.GetHoverAsync(hoverParams.Position);
        }

        Log.Information("Hover request processed. Hover being sent: {result}", result);
        return result;
    }

    [JsonRpcMethod(Methods.TextDocumentPublishDiagnosticsName)]
    public async Task PublishDiagnosticsAsync(PublishDiagnosticParams publishParams)
    {
        // Your implementation here.
        await Rpc.NotifyWithParameterObjectAsync(Methods.TextDocumentPublishDiagnosticsName, publishParams);
    }

    private bool IsScriptFile(Uri documentUri)
    {
        return Path.GetExtension(documentUri.AbsolutePath) == ".gsc" ||
            Path.GetExtension(documentUri.AbsolutePath) == ".csc";
    }
}