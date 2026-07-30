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
            // The KEY namespace, not the declared one: a merge dialect keys functions by bare
            // name, but still reports a namespace (the file stem), so using it here looked up a
            // key nothing is stored under and every lens read "0 references".
            SymbolKey key = new(GSCode.Core.GameProfile.Active.KeyNamespace(function.Namespace), function.KeyName, SymbolKind.Function);
            lenses.Add(MakeLens(request.TextDocument.Uri, function.NameRange, key, target));
        }

        foreach ( ClassSymbol classSymbol in target.Result.Extraction.Classes )
        {
            // A class key carries NO namespace, for the same reason the function key above carries
            // the KEY one rather than the declared one: it has to match what uses are stored under.
            // A class name is global in T7 — `new Throttle()` names it bare and there is no
            // `ns::Throttle` — so KeyNamespace is the wrong question to ask here. Asking it made
            // every class lens read "0 references".
            SymbolKey key = new(null, classSymbol.KeyName, SymbolKind.Class);
            lenses.Add(MakeLens(request.TextDocument.Uri, classSymbol.NameRange, key, target));
        }

        // Macros defined in THIS file. Headers are mostly macros, so without this a .gsh carried
        // no lenses at all. Inserted macros belong to the header that defines them, not here.
        foreach ( GSCode.Parser.Preprocessing.MacroDefinition macro in target.Result.Preprocessed.Macros.All )
        {
            if ( macro.SourceFile is not null )
            {
                continue;
            }

            SymbolKey key = new(null, macro.Name, SymbolKind.Macro);
            lenses.Add(MakeLens(request.TextDocument.Uri, macro.NameRange, key, target));
        }

        return Task.FromResult<CodeLensContainer?>(new CodeLensContainer(lenses));
    }

    public override Task<CodeLens> Handle(CodeLens request, CancellationToken cancellationToken)
    {
        return Task.FromResult(request);
    }

    private CodeLens MakeLens(DocumentUri uri, GSCode.Core.Text.TextRange nameRange, SymbolKey key, NavigationTarget target)
    {
        // The same query the peek list uses, so the number and the list cannot disagree. A
        // single-store count under-reported a function called from CSC or a macro used from a
        // header, while clicking the lens went through the client's reference provider.
        int count = 0;
        foreach ( (ScriptRecord _, ReferenceEntry entry) in _support.FindAllReferences(target, key) )
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
                Arguments = ShowReferencesArguments(uri, nameRange.Start),
            },
        };
    }

    /// <summary>
    /// The arguments for the client's gscode.showReferences bridge: a URI string and the position
    /// as two NUMBERS.
    ///
    /// Primitives only, deliberately. Arguments is a JArray, so whatever goes in is serialized
    /// as-is — OmniSharp's camelCase resolver never rewrites an already-materialized JToken.
    /// Passing a Position through JToken.FromObject therefore put {"Line":..,"Character":..} on
    /// the wire; the client read `position.line` as undefined and the vscode.Position constructor
    /// threw "Unexpected type". Numbers cannot be case-mangled by any serializer configuration.
    /// </summary>
    internal static JArray ShowReferencesArguments(DocumentUri uri, GSCode.Core.Text.Position start)
    {
        return new JArray(uri.ToString(), start.Line, start.Character);
    }
}
