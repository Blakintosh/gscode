using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Workspace.Database;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using SymbolKind = GSCode.Core.Symbols.SymbolKind;

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
        NavigationTarget? target = _support.Resolve(request.TextDocument.Uri);
        if ( target is null )
        {
            return Task.FromResult<Container<TypeHierarchyItem>?>(null);
        }

        PositionHit hit = SymbolAtPosition.Resolve(target.Result, request.Position.ToCore());
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
            new Container<TypeHierarchyItem>(MakeItem(classes[0].Class, classes[0].Record, target)));
    }

    public override Task<Container<TypeHierarchyItem>?> Handle(TypeHierarchySupertypesParams request, CancellationToken cancellationToken)
    {
        NavigationTarget? target = ResolveFromItem(request.Item);
        ClassSymbol? self = ClassFromItem(request.Item, target);
        if ( target is null || self?.ParentKeyName is null )
        {
            return Task.FromResult<Container<TypeHierarchyItem>?>(new Container<TypeHierarchyItem>());
        }

        ImmutableArray<ResolvedClass> parents = DatabaseQueries.LookupClasses(target.Store, target.ContextId, null, self.ParentKeyName);
        List<TypeHierarchyItem> items = [.. parents.Select(parent => MakeItem(parent.Class, parent.Record, target))];
        return Task.FromResult<Container<TypeHierarchyItem>?>(new Container<TypeHierarchyItem>(items));
    }

    public override Task<Container<TypeHierarchyItem>?> Handle(TypeHierarchySubtypesParams request, CancellationToken cancellationToken)
    {
        NavigationTarget? target = ResolveFromItem(request.Item);
        ClassSymbol? self = ClassFromItem(request.Item, target);
        if ( target is null || self is null )
        {
            return Task.FromResult<Container<TypeHierarchyItem>?>(new Container<TypeHierarchyItem>());
        }

        List<TypeHierarchyItem> items = [];
        foreach ( ScriptRecord record in target.Store.AllRecords )
        {
            if ( !ScriptDatabase.CanSee(target.ContextId, record.ContextId) )
            {
                continue;
            }

            foreach ( ClassSymbol candidate in record.Classes )
            {
                if ( string.Equals(candidate.ParentKeyName, self.KeyName, StringComparison.Ordinal) )
                {
                    items.Add(MakeItem(candidate, record, target));
                }
            }
        }

        return Task.FromResult<Container<TypeHierarchyItem>?>(new Container<TypeHierarchyItem>(items));
    }

    private NavigationTarget? ResolveFromItem(TypeHierarchyItem item)
    {
        return _support.Resolve(item.Uri);
    }

    private ClassSymbol? ClassFromItem(TypeHierarchyItem item, NavigationTarget? target)
    {
        if ( target is null )
        {
            return null;
        }

        string keyName = item.Name.ToLowerInvariant();
        ImmutableArray<ResolvedClass> classes = DatabaseQueries.LookupClasses(target.Store, target.ContextId, null, keyName);
        return classes.Length > 0 ? classes[0].Class : null;
    }

    private static TypeHierarchyItem MakeItem(ClassSymbol classSymbol, ScriptRecord record, NavigationTarget target)
    {
        OmniSharp.Extensions.LanguageServer.Protocol.Models.Range nameRange = classSymbol.NameRange.ToLsp();
        return new TypeHierarchyItem
        {
            Name = classSymbol.Name,
            Kind = OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind.Class,
            Uri = DocumentUri.FromFileSystemPath(record.Path),
            Range = classSymbol.FullRange.ToLsp(),
            SelectionRange = nameRange,
        };
    }
}
