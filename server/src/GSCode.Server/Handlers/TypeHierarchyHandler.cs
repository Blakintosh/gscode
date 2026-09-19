using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Workspace.Database;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using LspSymbolKind = OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind;
using SymbolKind = GSCode.Core.Symbols.SymbolKind;
using GSCode.Core;

namespace GSCode.Server.Handlers;

/// <summary>
/// Class type hierarchy: supertypes walk `ClassSymbol.Parent`, subtypes are the classes
/// whose parent is this class. Single inheritance keeps supertypes at most one per level.
/// </summary>
public sealed class TypeHierarchyHandler : TypeHierarchyHandlerBase
{
    private readonly NavigationSupport _support;
    private readonly TextDocumentSelector _selector;

    public TypeHierarchyHandler(NavigationSupport support, TextDocumentSelector selector)
    {
        _support = support;
        _selector = selector;
    }

    protected override TypeHierarchyRegistrationOptions CreateRegistrationOptions(TypeHierarchyCapability capability, ClientCapabilities clientCapabilities)
    {
        return new TypeHierarchyRegistrationOptions { DocumentSelector = _selector };
    }

    public override Task<Container<TypeHierarchyItem>?> Handle(TypeHierarchyPrepareParams request, CancellationToken cancellationToken)
    {
        NavigationTarget? target = _support.Resolve(request.TextDocument.Uri, cancellationToken);
        if ( target is null )
        {
            return Task.FromResult<Container<TypeHierarchyItem>?>(null);
        }

        PositionHit hit = _support.ResolveHit(target, request.Position.ToCore());
        if ( hit.Kind != HitKind.Reference || hit.Key.Kind != SymbolKind.Class )
        {
            return Task.FromResult<Container<TypeHierarchyItem>?>(null);
        }

        ImmutableArray<ResolvedClass> classes = DatabaseQueries.LookupClasses(target.Store, target.ContextId, hit.Key.Namespace, hit.Key.Name);
        if ( classes.Length == 0 )
        {
            return Task.FromResult<Container<TypeHierarchyItem>?>(null);
        }

        return Task.FromResult<Container<TypeHierarchyItem>?>(
            new Container<TypeHierarchyItem>(MakeItem(classes[0].Class, classes[0].Record)));
    }

    public override Task<Container<TypeHierarchyItem>?> Handle(TypeHierarchySupertypesParams request, CancellationToken cancellationToken)
    {
        SymbolQueryContext? target = ResolveFromItem(request.Item, cancellationToken);
        ClassSymbol? self = ClassFromItem(request.Item, target);
        if ( target is null || self?.ParentKeyName is null )
        {
            return Task.FromResult<Container<TypeHierarchyItem>?>(new Container<TypeHierarchyItem>());
        }

        ImmutableArray<ResolvedClass> parents = DatabaseQueries.LookupClasses(target.Store, target.ContextId, null, self.ParentKeyName);
        List<TypeHierarchyItem> items = [.. parents.Select(parent => MakeItem(parent.Class, parent.Record))];
        return Task.FromResult<Container<TypeHierarchyItem>?>(new Container<TypeHierarchyItem>(items));
    }

    public override Task<Container<TypeHierarchyItem>?> Handle(TypeHierarchySubtypesParams request, CancellationToken cancellationToken)
    {
        SymbolQueryContext? target = ResolveFromItem(request.Item, cancellationToken);
        ClassSymbol? self = ClassFromItem(request.Item, target);
        if ( target is null || self is null )
        {
            return Task.FromResult<Container<TypeHierarchyItem>?>(new Container<TypeHierarchyItem>());
        }

        // The graph knows who inherits from this class, so the subtype query is a lookup of the
        // child names followed by a resolve of each — not a scan of every record in the store.
        List<TypeHierarchyItem> items = [];
        foreach ( string childName in target.Store.Classes.DirectChildren(self.KeyName) )
        {
            foreach ( ResolvedClass child in DatabaseQueries.LookupClasses(
                target.Store, target.ContextId, namespaceName: null, childName) )
            {
                items.Add(MakeItem(child.Class, child.Record));
            }
        }

        return Task.FromResult<Container<TypeHierarchyItem>?>(new Container<TypeHierarchyItem>(items));
    }

    /// <summary>
    /// The file an item names, open or not.
    ///
    /// A supertype or subtype almost never lives in a file the user has open, and resolving through
    /// the document store returned null for those — which the protocol reads as "there are none".
    /// </summary>
    private SymbolQueryContext? ResolveFromItem(TypeHierarchyItem item, CancellationToken cancellationToken)
    {
        return _support.ResolveForQuery(item.Uri, cancellationToken);
    }

    private static ClassSymbol? ClassFromItem(TypeHierarchyItem item, SymbolQueryContext? target)
    {
        if ( target is null )
        {
            return null;
        }

        string keyName = NameTable.Shared.InternLower(item.Name);
        ImmutableArray<ResolvedClass> classes = DatabaseQueries.LookupClasses(target.Store, target.ContextId, null, keyName);
        return classes.Length > 0 ? classes[0].Class : null;
    }

    private static TypeHierarchyItem MakeItem(ClassSymbol classSymbol, ScriptRecord record)
    {
        LspRange nameRange = classSymbol.NameRange.ToLsp();
        return new TypeHierarchyItem
        {
            Name = classSymbol.Name,
            Kind = LspSymbolKind.Class,
            Uri = DocumentUri.FromFileSystemPath(record.Path),
            Range = classSymbol.FullRange.ToLsp(),
            SelectionRange = nameRange,
        };
    }
}
