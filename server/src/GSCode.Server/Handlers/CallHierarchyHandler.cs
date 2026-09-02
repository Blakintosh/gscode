using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Workspace.Database;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using JToken = Newtonsoft.Json.Linq.JToken;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using LspSymbolKind = OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind;
using Position = GSCode.Core.Text.Position;
using SymbolKind = GSCode.Core.Symbols.SymbolKind;
using TextRange = GSCode.Core.Text.TextRange;

namespace GSCode.Server.Handlers;

/// <summary>
/// Call hierarchy over the reference index: incoming calls are the callers of a function;
/// outgoing calls are the functions a function body calls. The item's data carries the
/// SymbolKey so the incoming/outgoing steps can resolve without re-reading the position.
/// </summary>
public sealed class CallHierarchyHandler : CallHierarchyHandlerBase
{
    private readonly NavigationSupport _support;
    private readonly TextDocumentSelector _selector;

    public CallHierarchyHandler(NavigationSupport support, TextDocumentSelector selector)
    {
        _support = support;
        _selector = selector;
    }

    protected override CallHierarchyRegistrationOptions CreateRegistrationOptions(CallHierarchyCapability capability, ClientCapabilities clientCapabilities)
    {
        return new CallHierarchyRegistrationOptions { DocumentSelector = _selector };
    }

    public override Task<Container<CallHierarchyItem>?> Handle(CallHierarchyPrepareParams request, CancellationToken cancellationToken)
    {
        NavigationTarget? target = _support.Resolve(request.TextDocument.Uri);
        if ( target is null )
        {
            return Task.FromResult<Container<CallHierarchyItem>?>(null);
        }

        PositionHit hit = SymbolAtPosition.Resolve(target.Result, request.Position.ToCore());
        if ( hit.Kind != HitKind.Reference || hit.Key.Kind != SymbolKind.Function )
        {
            return Task.FromResult<Container<CallHierarchyItem>?>(null);
        }

        // Anchor the item at the function's definition.
        foreach ( (ScriptRecord record, ReferenceEntry entry) in _support.FindAllReferences(target, hit.Key, hit.ReferenceKind) )
        {
            if ( entry.Kind == ReferenceKind.Definition )
            {
                return Task.FromResult<Container<CallHierarchyItem>?>(
                    new Container<CallHierarchyItem>(MakeItem(hit.Key, record, entry.Range)));
            }
        }

        return Task.FromResult<Container<CallHierarchyItem>?>(null);
    }

    public override Task<Container<CallHierarchyIncomingCall>?> Handle(CallHierarchyIncomingCallsParams request, CancellationToken cancellationToken)
    {
        NavigationTarget? target = _support.Resolve(new Uri(request.Item.Uri.ToString()));
        SymbolKey? key = KeyFromData(request.Item);
        if ( target is null || key is null )
        {
            return Task.FromResult<Container<CallHierarchyIncomingCall>?>(null);
        }

        // Callers: group non-definition references of this function by their containing file.
        Dictionary<string, (ScriptRecord Record, List<LspRange> Ranges)> byCaller = new(StringComparer.Ordinal);
        foreach ( (ScriptRecord record, ReferenceEntry entry) in _support.FindAllReferences(target, key.Value) )
        {
            if ( entry.Kind == ReferenceKind.Definition )
            {
                continue;
            }

            if ( !byCaller.TryGetValue(record.Path, out (ScriptRecord Record, List<LspRange> Ranges) group) )
            {
                group = (record, []);
                byCaller[record.Path] = group;
            }

            group.Ranges.Add(entry.Range.ToLsp());
        }

        List<CallHierarchyIncomingCall> incoming = [];
        foreach ( (ScriptRecord record, List<LspRange> ranges) in byCaller.Values )
        {
            // Attribute the call to the function whose body contains the first call range.
            FunctionSymbol? caller = ContainingFunction(record, ranges[0]);
            CallHierarchyItem item = caller is not null
                ? MakeItem(new SymbolKey(caller.Namespace.Length > 0 ? caller.Namespace : null, caller.KeyName, SymbolKind.Function), record, caller.NameRange.ToLsp())
                : MakeFileItem(record);

            incoming.Add(new CallHierarchyIncomingCall { From = item, FromRanges = new Container<LspRange>(ranges) });
        }

        return Task.FromResult<Container<CallHierarchyIncomingCall>?>(new Container<CallHierarchyIncomingCall>(incoming));
    }

    public override Task<Container<CallHierarchyOutgoingCall>?> Handle(CallHierarchyOutgoingCallsParams request, CancellationToken cancellationToken)
    {
        NavigationTarget? target = _support.Resolve(new Uri(request.Item.Uri.ToString()));
        SymbolKey? key = KeyFromData(request.Item);
        if ( target is null || key is null )
        {
            return Task.FromResult<Container<CallHierarchyOutgoingCall>?>(null);
        }

        // Find this function's definition record, then the call references inside its body range.
        ImmutableArray<ResolvedFunction> functions = DatabaseQueries.LookupFunctions(
            target.Store, target.ContextId, target.Path, key.Value.Namespace, key.Value.Name, askingNamespaces: target.Namespaces);
        if ( functions.Length == 0 )
        {
            return Task.FromResult<Container<CallHierarchyOutgoingCall>?>(null);
        }

        ResolvedFunction self = functions[0];
        Dictionary<SymbolKey, List<LspRange>> calls = new();
        foreach ( ReferenceEntry entry in self.Record.References )
        {
            // !entry.FromMacro preserves what the collapsed kind used to do here. An expanded call
            // is keyed to its INVOCATION range, so a macro used twice in one function would list the
            // same outgoing edge at ranges that spell the macro's name, not the callee's.
            if ( entry.Kind == ReferenceKind.Call && !entry.FromMacro && entry.Key.Kind == SymbolKind.Function
                && self.Function.FullRange.Contains(entry.Range.Start) )
            {
                if ( !calls.TryGetValue(entry.Key, out List<LspRange>? ranges) )
                {
                    ranges = [];
                    calls[entry.Key] = ranges;
                }

                ranges.Add(entry.Range.ToLsp());
            }
        }

        List<CallHierarchyOutgoingCall> outgoing = [];
        foreach ( (SymbolKey callee, List<LspRange> ranges) in calls )
        {
            ImmutableArray<ResolvedFunction> resolved = DatabaseQueries.LookupFunctions(
                target.Store, target.ContextId, target.Path, callee.Namespace, callee.Name, askingNamespaces: target.Namespaces);
            if ( resolved.Length == 0 )
            {
                continue;
            }

            CallHierarchyItem item = MakeItem(callee, resolved[0].Record, resolved[0].Function.NameRange.ToLsp());
            outgoing.Add(new CallHierarchyOutgoingCall { To = item, FromRanges = new Container<LspRange>(ranges) });
        }

        return Task.FromResult<Container<CallHierarchyOutgoingCall>?>(new Container<CallHierarchyOutgoingCall>(outgoing));
    }

    private static FunctionSymbol? ContainingFunction(ScriptRecord record, LspRange range)
    {
        Position start = range.Start.ToCore();
        foreach ( FunctionSymbol function in record.Functions )
        {
            if ( function.FullRange.Contains(start) )
            {
                return function;
            }
        }

        return null;
    }

    private static CallHierarchyItem MakeItem(SymbolKey key, ScriptRecord record, TextRange nameRange)
    {
        return MakeItem(key, record, nameRange.ToLsp());
    }

    private static CallHierarchyItem MakeItem(SymbolKey key, ScriptRecord record, LspRange nameRange)
    {
        return new CallHierarchyItem
        {
            Name = key.Name,
            Kind = LspSymbolKind.Function,
            Uri = DocumentUri.FromFileSystemPath(record.Path),
            Range = nameRange,
            SelectionRange = nameRange,
            Data = JToken.FromObject(new { ns = key.Namespace ?? "", name = key.Name }),
        };
    }

    private static CallHierarchyItem MakeFileItem(ScriptRecord record)
    {
        LspRange zero = new(0, 0, 0, 0);
        return new CallHierarchyItem
        {
            Name = System.IO.Path.GetFileName(record.Path),
            Kind = LspSymbolKind.File,
            Uri = DocumentUri.FromFileSystemPath(record.Path),
            Range = zero,
            SelectionRange = zero,
        };
    }

    private static SymbolKey? KeyFromData(CallHierarchyItem item)
    {
        if ( item.Data is null )
        {
            return null;
        }

        string ns = item.Data["ns"]?.ToString() ?? "";
        string name = item.Data["name"]?.ToString() ?? "";
        if ( name.Length == 0 )
        {
            return null;
        }

        return new SymbolKey(ns.Length > 0 ? ns : null, name, SymbolKind.Function);
    }
}
