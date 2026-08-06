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
    /// Narrows definitions to the file the call actually reaches. A path call
    /// (<c>maps\x::foo()</c>) names ONE file, so it pins to that; anything else prefers the asking
    /// file's import scope — itself plus the files it links against. Either way it is a
    /// PREFERENCE — the full set comes back when nothing matches, so a missing import still
    /// resolves.
    ///
    /// Both dialect families need this, for the same reason arrived at differently. A merge
    /// dialect drops the namespace from the key outright, so every same-named function in the
    /// workspace collapses into one. A namespace-driven dialect (BO3) keeps the namespace, but a
    /// namespace is NOT unique to a file: <c>scripts\mp\gametypes\_globallogic_utils.gsc</c> and
    /// <c>scripts\zm\gametypes\_globallogic_utils.gsc</c> both declare <c>#namespace
    /// globallogic_utils</c>, so the key <c>globallogic_utils::func</c> names two declarations and
    /// go-to-definition offered the ZM file's caller both. The stock scripts hold 565 such pairs —
    /// see <see cref="GSCode.Workspace.Analysis.AmbiguousFunctionLint"/>, which counts them —
    /// almost all of them one game mode's copy against another's.
    ///
    /// Which directive spells the scope is the only difference, and
    /// <see cref="DatabaseQueries.LinkedScriptPaths"/> owns that choice so it is made once. Both
    /// are DIRECT-only. BO3 does not re-export what an imported file itself imports, and a merge
    /// dialect's transitive reach is deliberately not used here (preferring too little only costs
    /// a wider answer, which the fallback already allows).
    /// </summary>
    private ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> ScopeToIncludes(
        NavigationTarget target, PositionHit hit, ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> definitions)
    {
        // A path call names its target file explicitly — pin to it (as its own single-file scope).
        // Empty on BO3, which has no inline path calls, so the loop costs nothing there.
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
            definitions, selfRelative, DatabaseQueries.LinkedScriptPaths(target.Result));
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
