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

        bool includeDeclaration = request.Context?.IncludeDeclaration ?? true;

        if ( hit.Kind != HitKind.Reference )
        {
            // Not something the reference index knows, which is every LOCAL: the index is keyed by
            // SymbolKey and shared workspace-wide, so an `i` in one function would collide with an
            // `i` in every other. Locals are walked from the AST instead, per function, which is
            // the scope they actually have — the same fallthrough DefinitionHandler takes.
            return Task.FromResult(LocalReferencesAt(target, request.Position.ToCore(), includeDeclaration));
        }

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

    /// <summary>
    /// Every occurrence of the local under the cursor, all of them in this one file.
    ///
    /// Only the DECLARATION is dropped when the request asks to exclude it — the parameter, or the
    /// first write. A later `x = 2` is a reference to a variable that already exists, so dropping
    /// every write would hide most of what was asked for.
    /// </summary>
    private LocationContainer? LocalReferencesAt(
        NavigationTarget target, GSCode.Core.Text.Position position, bool includeDeclaration)
    {
        List<Location> locations = [];
        foreach ( LocalOccurrence occurrence in _support.LocalOccurrencesAt(target, position) )
        {
            if ( !includeDeclaration && occurrence.IsDeclaration )
            {
                continue;
            }

            locations.Add(LspMapping.LocationAt(target.Path, occurrence.Range));
        }

        if ( locations.Count == 0 )
        {
            return null;
        }

        return new LocationContainer(locations);
    }
}
