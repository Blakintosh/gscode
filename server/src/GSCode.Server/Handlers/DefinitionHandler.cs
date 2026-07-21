using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Workspace.Database;
using GSCode.Workspace.Resolution;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Server.Handlers;

/// <summary>
/// Go-to-definition for functions, classes, and macros (via their Definition references),
/// plus #using/#insert paths jumping to the target file. Builtins have no definition.
/// </summary>
public sealed class DefinitionHandler : DefinitionHandlerBase
{
    private readonly NavigationSupport _support;
    private readonly TextDocumentSelector _selector;

    public DefinitionHandler(NavigationSupport support, TextDocumentSelector selector)
    {
        _support = support;
        _selector = selector;
    }

    protected override DefinitionRegistrationOptions CreateRegistrationOptions(DefinitionCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DefinitionRegistrationOptions { DocumentSelector = _selector };
    }

    public override Task<LocationOrLocationLinks?> Handle(DefinitionParams request, CancellationToken cancellationToken)
    {
        NavigationTarget? target = _support.Resolve(request.TextDocument.Uri);
        if ( target is null )
        {
            return Task.FromResult<LocationOrLocationLinks?>(null);
        }

        PositionHit hit = SymbolAtPosition.Resolve(target.Result, request.Position.ToCore());

        if ( hit.Kind == HitKind.DependencyPath )
        {
            string? path = ResolveDependencyPath(target, hit);
            if ( path is null )
            {
                return Task.FromResult<LocationOrLocationLinks?>(null);
            }

            Location fileStart = new()
            {
                Uri = DocumentUri.FromFileSystemPath(path),
                Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(0, 0, 0, 0),
            };
            return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks(fileStart));
        }

        if ( hit.Kind != HitKind.Reference )
        {
            return Task.FromResult<LocationOrLocationLinks?>(null);
        }

        List<Location> definitions = [];
        foreach ( (ScriptRecord record, ReferenceEntry entry) in DefinitionSources(target, hit.Key) )
        {
            if ( entry.Kind == ReferenceKind.Definition )
            {
                definitions.Add(new Location
                {
                    Uri = DocumentUri.FromFileSystemPath(record.Path),
                    Range = entry.Range.ToLsp(),
                });
            }
        }

        if ( definitions.Count == 0 )
        {
            return Task.FromResult<LocationOrLocationLinks?>(null);
        }

        return Task.FromResult<LocationOrLocationLinks?>(
            new LocationOrLocationLinks(definitions.Select(location => new LocationOrLocationLink(location))));
    }

    /// <summary>
    /// Where a definition may live. Macros additionally search the shared GSH store, because a
    /// macro declared in a header is recorded there rather than in either language store —
    /// without this, go-to-definition on an inserted macro finds nothing at all.
    /// </summary>
    private ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> DefinitionSources(NavigationTarget target, SymbolKey key)
    {
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> inLanguage =
            DatabaseQueries.FindReferences(target.Store, target.ContextId, key);

        if ( key.Kind != GSCode.Core.Symbols.SymbolKind.Macro )
        {
            return inLanguage;
        }

        return inLanguage.AddRange(DatabaseQueries.FindGshReferences(_support.Database, target.ContextId, key));
    }

    private string? ResolveDependencyPath(NavigationTarget target, PositionHit hit)
    {
        if ( hit.DependencyTargetPath.Length > 0 )
        {
            return hit.DependencyTargetPath;
        }

        // A #using with no pre-resolved path: resolve it now against this file's context.
        foreach ( GSCode.Parser.Syntax.Ast.AstNode element in target.Result.Tree.Root.Elements )
        {
            if ( element is GSCode.Parser.Syntax.Ast.UsingNode usingNode && usingNode.PathRange == hit.Range )
            {
                ResolutionContext context = _support.Resolver.GetContext(target.Path);
                string extension = target.Language == ScriptLanguage.Csc ? ".csc" : ".gsc";
                return _support.Resolver.Resolve(context, usingNode.Path + extension);
            }
        }

        return null;
    }
}
