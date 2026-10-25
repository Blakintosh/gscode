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
/// "N references" lenses above each function and class declaration; clicking invokes the
/// gscode.showReferences client bridge.
///
/// Listed without counts and counted on RESOLVE. The editor resolves the lenses it is showing, and a
/// count runs the workspace reference query: at 50,000 heavy BO3 files, counting all 125 lenses of a
/// scene_shared.gsc copy on opening it took about 90 ms p99, for a screen that shows a handful.
/// </summary>
public sealed class CodeLensHandler : CodeLensHandlerBase
{
    private readonly NavigationSupport _support;
    private readonly ServerSettings _settings;
    private readonly TextDocumentSelector _selector;
    private readonly LensCountCache _counts;

    public CodeLensHandler(
        NavigationSupport support, ServerSettings settings, TextDocumentSelector selector, LensCountCache counts)
    {
        _support = support;
        _settings = settings;
        _selector = selector;
        _counts = counts;
    }

    protected override CodeLensRegistrationOptions CreateRegistrationOptions(CodeLensCapability capability, ClientCapabilities clientCapabilities)
    {
        return new CodeLensRegistrationOptions { DocumentSelector = _selector, ResolveProvider = true };
    }

    public override Task<CodeLensContainer?> Handle(CodeLensParams request, CancellationToken cancellationToken)
    {
        if ( !_settings.CodeLensEnabled )
        {
            return Task.FromResult<CodeLensContainer?>(new CodeLensContainer());
        }

        // ResolveFresh, not Resolve: lens ranges are positional, and serving them from the
        // debounce-stale analysis put every lens one edit behind the buffer — the rows jumped on
        // each keystroke and dragged the viewport with them.
        NavigationTarget? target = _support.ResolveFresh(request.TextDocument.Uri, cancellationToken);
        if ( target is null )
        {
            return Task.FromResult<CodeLensContainer?>(null);
        }

        List<CodeLens> lenses = [];

        // Checked per lens, not per request: a file can declare a few hundred names. The client
        // re-requests lenses after every analysis that moves a file's exports, and cancels the
        // request it has superseded.
        foreach ( FunctionSymbol function in target.Result.Extraction.Functions )
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The KEY namespace, not the declared one: a merge dialect keys functions by bare
            // name, but still reports a namespace (the file stem), so using it here looked up a
            // key nothing is stored under and every lens read "0 references".
            SymbolKey key = new(GSCode.Core.GameProfile.Active.KeyNamespace(function.Namespace), function.KeyName, SymbolKind.Function);
            lenses.Add(Unresolved(request.TextDocument.Uri, function.NameRange, key, function.IsAutoexec));
        }

        foreach ( ClassSymbol classSymbol in target.Result.Extraction.Classes )
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A class key carries NO namespace, for the same reason the function key above carries
            // the KEY one rather than the declared one: it has to match what uses are stored under.
            // A class name is global in T7 — `new Throttle()` names it bare and there is no
            // `ns::Throttle` — so KeyNamespace is the wrong question to ask here. Asking it made
            // every class lens read "0 references".
            SymbolKey key = new(null, classSymbol.KeyName, SymbolKind.Class);
            lenses.Add(Unresolved(request.TextDocument.Uri, classSymbol.NameRange, key));

            // METHODS. The loop above walks Extraction.Functions, which holds top-level functions
            // only — a method lives on its class — so without this a class body shows only the
            // class's own lens.
            //
            // Keyed with the declaring class and no namespace, matching what extraction writes for a
            // declaration, so the count runs the same query the peek list does.
            foreach ( FunctionSymbol method in classSymbol.Methods )
            {
                cancellationToken.ThrowIfCancellationRequested();

                SymbolKey methodKey = new(null, method.KeyName, SymbolKind.Function, classSymbol.KeyName);
                lenses.Add(Unresolved(request.TextDocument.Uri, method.NameRange, methodKey));
            }

            // Constructors and destructors deliberately get none: neither is callable by name, so a
            // reference count on one could only ever read zero.
        }

        // Macros defined in THIS file. Headers are mostly macros, so without this a .gsh carried
        // no lenses at all. Inserted macros belong to the header that defines them, not here.
        foreach ( GSCode.Parser.Preprocessing.MacroDefinition macro in target.Result.Preprocessed.Macros.All )
        {
            cancellationToken.ThrowIfCancellationRequested();

            if ( macro.SourceFile is not null )
            {
                continue;
            }

            SymbolKey key = new(null, macro.Name, SymbolKind.Macro);
            lenses.Add(Unresolved(request.TextDocument.Uri, macro.NameRange, key));
        }

        return Task.FromResult<CodeLensContainer?>(new CodeLensContainer(lenses));
    }

    /// <summary>Counts one lens the editor is showing: see the class summary.</summary>
    public override Task<CodeLens> Handle(CodeLens request, CancellationToken cancellationToken)
    {
        if ( request.Command is not null || request.Data is not JObject data )
        {
            return Task.FromResult(request);
        }

        DocumentUri uri = DocumentUri.From(data.Value<string>("uri")!);
        NavigationTarget? target = _support.ResolveFresh(uri, cancellationToken);
        if ( target is null )
        {
            return Task.FromResult(request);
        }

        string ns = data.Value<string>("ns") ?? "";
        string owner = data.Value<string>("owner") ?? "";
        SymbolKey key = new(
            ns.Length == 0 ? null : ns,
            data.Value<string>("name")!,
            (SymbolKind)data.Value<int>("kind"),
            owner.Length == 0 ? null : owner);

        int count = CountReferences(target, key, _counts.For(target));
        return Task.FromResult(Counted(uri, request.Range, count, data.Value<bool>("autoexec")));
    }

    /// <summary>
    /// The list and every lens in it counted: what a client that never resolves would need, and how
    /// the tests and the measurements that want every count ask.
    /// </summary>
    internal async Task<CodeLensContainer?> HandleResolvedAsync(CodeLensParams request, CancellationToken cancellationToken)
    {
        CodeLensContainer? listed = await Handle(request, cancellationToken);
        if ( listed is null )
        {
            return null;
        }

        List<CodeLens> counted = [];
        foreach ( CodeLens lens in listed )
        {
            counted.Add(await Handle(lens, cancellationToken));
        }

        return new CodeLensContainer(counted);
    }

    /// <summary>
    /// A lens with what resolving it needs and no count. A key's namespace and owner are written as
    /// "" for none, so no null reaches the wire for a client to drop.
    /// </summary>
    private static CodeLens Unresolved(DocumentUri uri, GSCode.Core.Text.TextRange nameRange, SymbolKey key, bool isAutoexec = false)
    {
        return new CodeLens
        {
            Range = nameRange.ToLsp(),
            Data = new JObject
            {
                ["uri"] = uri.ToString(),
                ["kind"] = (int)key.Kind,
                ["ns"] = key.Namespace ?? "",
                ["name"] = key.Name,
                ["owner"] = key.OwnerClass ?? "",
                ["autoexec"] = isAutoexec,
            },
        };
    }

    private static CodeLens Counted(
        DocumentUri uri, OmniSharp.Extensions.LanguageServer.Protocol.Models.Range range, int count, bool isAutoexec)
    {
        string title = count == 1 ? "1 reference" : $"{count} references";

        // An autoexec function is called by the engine on load, so its call sites are not in any
        // script and the count is legitimately zero. Left unqualified that reads as dead code —
        // which is the one thing an autoexec entry point is not.
        if ( isAutoexec )
        {
            title = count == 0 ? "autoexec entry point" : $"{title} · autoexec";
        }

        return new CodeLens
        {
            Range = range,
            Command = new Command
            {
                Title = title,
                Name = "gscode.showReferences",
                Arguments = ShowReferencesArguments(uri, new GSCode.Core.Text.Position(range.Start.Line, range.Start.Character)),
            },
        };
    }

    /// <summary>
    /// The same query the peek list uses, so the number and the list cannot disagree. A
    /// single-store count under-reported a function called from CSC or a macro used from a header,
    /// while clicking the lens went through the client's reference provider.
    ///
    /// The other files' part comes from <paramref name="counts"/> when it holds the key; this file's
    /// own part is always counted again, by the same query narrowed to the file — see
    /// <see cref="LensCountCache"/>.
    /// </summary>
    private int CountReferences(NavigationTarget target, SymbolKey key, LensCountCache.Snapshot? counts)
    {
        if ( counts is null )
        {
            return CountUses(_support.FindAllReferences(target, key));
        }

        int inFile = CountUses(_support.FindReferencesInFile(target, key));
        if ( !counts.Outside.TryGetValue(key, out int outside) )
        {
            outside = CountUses(_support.FindAllReferences(target, key)) - inFile;
            counts.Outside[key] = outside;
        }

        return outside + inFile;
    }

    private static int CountUses(System.Collections.Immutable.ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> references)
    {
        int count = 0;
        foreach ( (ScriptRecord Record, ReferenceEntry Entry) reference in references )
        {
            if ( reference.Entry.Kind != ReferenceKind.Definition )
            {
                count++;
            }
        }

        return count;
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
