using GSCode.Core;
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
            // Not something the reference index knows, which is every LOCAL: the index is keyed by
            // SymbolKey and shared workspace-wide, so an `i` in one function would collide with an
            // `i` in every other. Locals are resolved from the AST instead, per function, which is
            // the scope they actually have.
            return Task.FromResult(LocalDefinitionAt(target, request.Position.ToCore()));
        }

        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> sources =
            [.. DefinitionSources(target, hit.Key, hit.ReferenceKind)
                .Where(static source => source.Entry.Kind == ReferenceKind.Definition)];

        sources = ScopeToIncludes(target, hit, sources);

        List<Location> definitions = [.. sources.Select(static source => new Location
        {
            Uri = DocumentUri.FromFileSystemPath(source.Record.Path),
            Range = source.Entry.Range.ToLsp(),
        })];

        if ( definitions.Count == 0 )
        {
            return Task.FromResult<LocationOrLocationLinks?>(null);
        }

        return Task.FromResult<LocationOrLocationLinks?>(
            new LocationOrLocationLinks(definitions.Select(location => new LocationOrLocationLink(location))));
    }

    /// <summary>The parameter or assignment that introduced the local under the cursor, if any.</summary>
    private static LocationOrLocationLinks? LocalDefinitionAt(
        NavigationTarget target, GSCode.Core.Text.Position position)
    {
        GSCode.Core.Text.TextRange? range = LocalDefinition.Find(target.Result, position);
        if ( range is null )
        {
            return null;
        }

        return new LocationOrLocationLinks(new Location
        {
            Uri = DocumentUri.FromFileSystemPath(target.Path),
            Range = range.Value.ToLsp(),
        });
    }

    /// <summary>
    /// Where a definition may live: the shared query, which already folds in the GSH store for
    /// macro keys (a macro declared in a header is recorded there rather than in either language
    /// store, and without it go-to-definition on an inserted macro finds nothing at all).
    /// </summary>
    private ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> DefinitionSources(
        NavigationTarget target, SymbolKey key, ReferenceKind referenceKind)
    {
        return _support.FindAllReferences(target, key, referenceKind);
    }

    /// <summary>
    /// On a merge dialect, narrows definitions to the file the call actually reaches. A path call
    /// (<c>maps\x::foo()</c>) names ONE file, so it pins to that; an unqualified call prefers the
    /// asking file's include scope (itself + its #included files). Either way it is a PREFERENCE —
    /// the full set comes back when nothing matches, so a missing #include still resolves. A
    /// namespace-driven dialect (BO3) already qualifies by namespace and needs no scoping.
    /// </summary>
    private ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> ScopeToIncludes(
        NavigationTarget target, PositionHit hit, ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> definitions)
    {
        if ( GameProfile.Active.ResolvesByNamespace )
        {
            return definitions;
        }

        // A path call names its target file explicitly — pin to it (as its own single-file scope).
        foreach ( GSCode.Parser.Extraction.PathCallReference pathCall in target.Result.Extraction.PathCalls )
        {
            if ( pathCall.NameRange == hit.Range )
            {
                return DatabaseQueries.PreferIncludeScope(definitions, pathCall.Path, includedPaths: []);
            }
        }

        ResolutionContext context = _support.Resolver.GetContext(target.Path);
        string selfRelative = _support.Resolver.GetScriptRelativePath(target.Path, context);
        return DatabaseQueries.PreferIncludeScope(
            definitions, selfRelative, DatabaseQueries.IncludedScriptPaths(target.Result));
    }

    private string? ResolveDependencyPath(NavigationTarget target, PositionHit hit)
    {
        if ( hit.DependencyTargetPath.Length > 0 )
        {
            return hit.DependencyTargetPath;
        }

        // A #using / #include with no pre-resolved path: resolve it now against this file's context.
        foreach ( GSCode.Parser.Syntax.Ast.AstNode element in target.Result.Tree.Root.Elements )
        {
            string? directivePath = element switch
            {
                GSCode.Parser.Syntax.Ast.UsingNode usingNode when usingNode.PathRange == hit.Range => usingNode.Path,
                GSCode.Parser.Syntax.Ast.IncludeNode includeNode when includeNode.PathRange == hit.Range => includeNode.Path,
                _ => null,
            };

            if ( directivePath is not null )
            {
                ResolutionContext context = _support.Resolver.GetContext(target.Path);
                string extension = target.Language == ScriptLanguage.Csc
                    ? GameProfile.Active.ClientScriptExtension
                    : GameProfile.Active.ServerScriptExtension;
                return _support.Resolver.Resolve(context, directivePath + extension);
            }
        }

        return null;
    }
}
