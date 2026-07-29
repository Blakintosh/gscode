using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Server.Mapping;
using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Server.Handlers;

/// <summary>
/// Validates a rename before the UI opens: returns the symbol's range for anything the scripts
/// define, or null so the editor shows "cannot rename here" for what the engine defines - builtins
/// and engine fields - and for keywords. Shares RenameHandler.IsRenameable so the preview and the
/// rename itself can never disagree about what is allowed.
/// </summary>
public sealed class PrepareRenameHandler : IPrepareRenameHandler
{
    private readonly NavigationSupport _support;
    private readonly BuiltinApiSet _builtins;
    private readonly ObjectFields _objectFields;

    public PrepareRenameHandler(NavigationSupport support, BuiltinApiSet builtins, ObjectFields objectFields)
    {
        _support = support;
        _builtins = builtins;
        _objectFields = objectFields;
    }

    public RenameRegistrationOptions GetRegistrationOptions(RenameCapability capability, ClientCapabilities clientCapabilities)
    {
        return new RenameRegistrationOptions { PrepareProvider = true };
    }

    public Task<RangeOrPlaceholderRange?> Handle(PrepareRenameParams request, CancellationToken cancellationToken)
    {
        NavigationTarget? target = _support.Resolve(request.TextDocument.Uri);
        if ( target is null )
        {
            return Task.FromResult<RangeOrPlaceholderRange?>(null);
        }

        PositionHit hit = SymbolAtPosition.Resolve(target.Result, request.Position.ToCore());
        if ( !RenameHandler.IsRenameable(hit, _builtins.For(target.Language), _objectFields) )
        {
            return Task.FromResult<RangeOrPlaceholderRange?>(null);
        }

        return Task.FromResult<RangeOrPlaceholderRange?>(new RangeOrPlaceholderRange(hit.Range.ToLsp()));
    }
}
