using GSCode.Core.Symbols;
using GSCode.Workspace.Database;
using GSCode.Server.Configuration;
using GSCode.Server.Mapping;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using SymbolKind = GSCode.Core.Symbols.SymbolKind;

namespace GSCode.Server.Handlers;

/// <summary>
/// "N references" lenses above each function and class declaration. Counts come from the
/// reference index (cheap); clicking invokes the gscode.showReferences client bridge.
/// </summary>
public sealed class CodeLensHandler : CodeLensHandlerBase
{
    private readonly NavigationSupport _support;
    private readonly ServerSettings _settings;
    private readonly TextDocumentSelector _selector;

    public CodeLensHandler(NavigationSupport support, ServerSettings settings, TextDocumentSelector selector)
    {
        _support = support;
        _settings = settings;
        _selector = selector;
    }

    protected override CodeLensRegistrationOptions CreateRegistrationOptions(CodeLensCapability capability, ClientCapabilities clientCapabilities)
    {
        return new CodeLensRegistrationOptions { DocumentSelector = _selector, ResolveProvider = false };
    }

    public override Task<CodeLensContainer?> Handle(CodeLensParams request, CancellationToken cancellationToken)
    {
        if ( !_settings.CodeLensEnabled )
        {
            return Task.FromResult<CodeLensContainer?>(new CodeLensContainer());
        }

        NavigationTarget? target = _support.Resolve(request.TextDocument.Uri);
        if ( target is null )
        {
            return Task.FromResult<CodeLensContainer?>(null);
        }

        List<CodeLens> lenses = [];

        foreach ( FunctionSymbol function in target.Result.Extraction.Functions )
        {
            SymbolKey key = new(function.Namespace.Length > 0 ? function.Namespace : null, function.KeyName, SymbolKind.Function);
            lenses.Add(MakeLens(request.TextDocument.Uri, function.NameRange, key, target));
        }

        foreach ( ClassSymbol classSymbol in target.Result.Extraction.Classes )
        {
            SymbolKey key = new(classSymbol.Namespace.Length > 0 ? classSymbol.Namespace : null, classSymbol.KeyName, SymbolKind.Class);
            lenses.Add(MakeLens(request.TextDocument.Uri, classSymbol.NameRange, key, target));
        }

        return Task.FromResult<CodeLensContainer?>(new CodeLensContainer(lenses));
    }

    public override Task<CodeLens> Handle(CodeLens request, CancellationToken cancellationToken)
    {
        return Task.FromResult(request);
    }

    private CodeLens MakeLens(DocumentUri uri, GSCode.Core.Text.TextRange nameRange, SymbolKey key, NavigationTarget target)
    {
        int count = 0;
        foreach ( (ScriptRecord _, ReferenceEntry entry) in DatabaseQueries.FindReferences(target.Store, target.ContextId, key) )
        {
            if ( entry.Kind != ReferenceKind.Definition )
            {
                count++;
            }
        }

        string title = count == 1 ? "1 reference" : $"{count} references";

        return new CodeLens
        {
            Range = nameRange.ToLsp(),
            Command = new Command
            {
                Title = title,
                Name = "gscode.showReferences",
                Arguments = new JArray(uri.ToString(), JToken.FromObject(nameRange.Start.ToLsp())),
            },
        };
    }
}
