using GSCode.Workspace.Database;
using GSCode.Server.Mapping;
using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Server.Handlers;

/// <summary>
/// Validates a rename before the UI opens: returns the symbol's range for renameable
/// symbols (functions/classes/macros), or null so the editor shows "cannot rename here"
/// for builtins, keywords, and literals.
/// </summary>
public sealed class PrepareRenameHandler : IPrepareRenameHandler
{
    private readonly NavigationSupport _support;

    public PrepareRenameHandler(NavigationSupport support)
    {
        _support = support;
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
        if ( !RenameHandler.IsRenameable(hit) )
        {
            return Task.FromResult<RangeOrPlaceholderRange?>(null);
        }

        return Task.FromResult<RangeOrPlaceholderRange?>(new RangeOrPlaceholderRange(hit.Range.ToLsp()));
    }
}
