using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Workspace.Database;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Server.Handlers;

/// <summary>
/// Find-all-references across the visible context, for functions/classes/macros/fields
/// and literal references (strings, hash strings, istrings, anim refs). The declaration
/// is included per the request's context.
/// </summary>
public sealed class ReferencesHandler : ReferencesHandlerBase
{
    private readonly NavigationSupport _support;
    private readonly TextDocumentSelector _selector;

    public ReferencesHandler(NavigationSupport support, TextDocumentSelector selector)
    {
        _support = support;
        _selector = selector;
    }

    protected override ReferenceRegistrationOptions CreateRegistrationOptions(ReferenceCapability capability, ClientCapabilities clientCapabilities)
    {
        return new ReferenceRegistrationOptions { DocumentSelector = _selector };
    }

    public override Task<LocationContainer?> Handle(ReferenceParams request, CancellationToken cancellationToken)
    {
        NavigationTarget? target = _support.Resolve(request.TextDocument.Uri);
        if ( target is null )
        {
            return Task.FromResult<LocationContainer?>(null);
        }

        PositionHit hit = SymbolAtPosition.Resolve(target.Result, request.Position.ToCore());
        if ( hit.Kind != HitKind.Reference )
        {
            return Task.FromResult<LocationContainer?>(null);
        }

        bool includeDeclaration = request.Context?.IncludeDeclaration ?? true;
        List<Location> locations = [];

        // On the merge dialects (#include) a function and its calls are keyed (null, name), so
        // unrelated files sharing a name share this key. FindAllReferences narrows that to the
        // declaration this document actually reaches — see NavigationSupport.DeclaringFile — and
        // does it inside the SHARED query, so this list and the CodeLens count stay in step.
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> found =
            _support.FindAllReferences(target, hit.Key, hit.ReferenceKind);

        foreach ( (ScriptRecord record, ReferenceEntry entry) in found )
        {
            if ( !includeDeclaration && entry.Kind == ReferenceKind.Definition )
            {
                continue;
            }

            locations.Add(LspMapping.LocationAt(record.Path, entry.Range));
        }

        return Task.FromResult<LocationContainer?>(new LocationContainer(locations));
    }
}
