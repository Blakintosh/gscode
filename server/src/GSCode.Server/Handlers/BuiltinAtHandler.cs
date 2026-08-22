using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Server.Mapping;
using MediatR;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol;

namespace GSCode.Server.Handlers;

/// <summary>Request for gscode/builtinAt: which engine function, if any, is under this position.</summary>
[Method("gscode/builtinAt", Direction.ClientToServer)]
public sealed class BuiltinAtParams : IRequest<BuiltinAtResponse>
{
    public string Uri { get; set; } = "";
    public int Line { get; set; }
    public int Character { get; set; }
}

/// <summary>Response for gscode/builtinAt.</summary>
public sealed class BuiltinAtResponse
{
    /// <summary>The builtin's name as the library spells it, or "" when the position is not one.</summary>
    public string Name { get; set; } = "";

    /// <summary>"gsc" or "csc" — the library the name belongs to.</summary>
    public string Language { get; set; } = "";
}

/// <summary>
/// Answers "is the thing under the cursor an engine function, and what is it called".
///
/// Exists so the client can open a symbol's own documentation page rather than the library index.
/// The extension host has no symbol knowledge at all — it cannot tell `LUINotifyEvent` from a
/// local variable — and resolution is not a question of text: a script function of the same name
/// SHADOWS the builtin, so the answer depends on the whole database.
///
/// Deliberately narrow. It returns a name, not a URL: how the site addresses its pages is the
/// client's business, and the server should not need redeploying if that changes.
/// </summary>
public sealed class BuiltinAtHandler : IJsonRpcRequestHandler<BuiltinAtParams, BuiltinAtResponse>
{
    private readonly NavigationSupport _support;
    private readonly BuiltinApiSet _builtins;

    public BuiltinAtHandler(NavigationSupport support, BuiltinApiSet builtins)
    {
        _support = support;
        _builtins = builtins;
    }

    public Task<BuiltinAtResponse> Handle(BuiltinAtParams request, CancellationToken cancellationToken)
    {
        BuiltinAtResponse none = new();

        NavigationTarget? target = _support.Resolve(DocumentUri.Parse(request.Uri));
        if ( target is null )
        {
            return Task.FromResult(none);
        }

        PositionHit hit = SymbolAtPosition.Resolve(
            target.Result, new GSCode.Core.Text.Position(request.Line, request.Character));

        if ( hit.Kind != HitKind.Reference || hit.Key.Kind != SymbolKind.Function )
        {
            return Task.FromResult(none);
        }

        // A script function of this name wins, exactly as it does for hover and go-to-definition.
        // Opening the engine's page for a name the workspace has redefined would be a lie.
        ImmutableArray<ResolvedFunction> functions = DatabaseQueries.LookupFunctions(
            target.Store, target.ContextId, target.Path, hit.Key.Namespace, hit.Key.Name,
            askingNamespaces: target.Namespaces);

        if ( functions.Length > 0 )
        {
            return Task.FromResult(none);
        }

        BuiltinFunction? builtin = _builtins.For(target.Language).Find(hit.Key.Name);
        if ( builtin is null )
        {
            return Task.FromResult(none);
        }

        return Task.FromResult(new BuiltinAtResponse
        {
            Name = builtin.Name,
            // A .gsh serves both worlds and has no library of its own; GSC is the larger one.
            Language = target.Language == ScriptLanguage.Csc ? "csc" : "gsc",
        });
    }
}
