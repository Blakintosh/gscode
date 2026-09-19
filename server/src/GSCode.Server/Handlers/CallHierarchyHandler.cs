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
        NavigationTarget? target = _support.Resolve(request.TextDocument.Uri, cancellationToken);
        if ( target is null )
        {
            return Task.FromResult<Container<CallHierarchyItem>?>(null);
        }

        PositionHit hit = _support.ResolveHit(target, request.Position.ToCore());
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
        // The item's own DocumentUri, not a round trip through System.Uri: DocumentUri.ToString
        // followed by new Uri(string) throws UriFormatException on input DocumentUri.Parse accepts,
        // out of a handler that catches nothing.
        //
        // ResolveForQuery rather than Resolve, because a caller is normally in a file the user does
        // NOT have open, and requiring an open document answered those with null — which the
        // protocol reads as "there are no incoming calls".
        SymbolQueryContext? target = _support.ResolveForQuery(request.Item.Uri, cancellationToken);
        SymbolKey? key = KeyFromData(request.Item);
        if ( target is null || key is null )
        {
            return Task.FromResult<Container<CallHierarchyIncomingCall>?>(null);
        }

        List<CallHierarchyIncomingCall> incoming = [];
        foreach ( IncomingGroup group in GroupIncomingCalls(_support.FindAllReferences(target, key.Value)) )
        {
            // A call outside every function body — a file-scope constant's initialiser, say — has
            // no calling function to name, so the file stands in for it.
            CallHierarchyItem item = group.Caller is not null
                ? MakeItem(
                    new SymbolKey(
                        group.Caller.Namespace.Length > 0 ? group.Caller.Namespace : null,
                        group.Caller.KeyName,
                        SymbolKind.Function),
                    group.Record,
                    group.Caller.NameRange.ToLsp())
                : MakeFileItem(group.Record);

            incoming.Add(new CallHierarchyIncomingCall { From = item, FromRanges = new Container<LspRange>(group.Ranges) });
        }

        return Task.FromResult<Container<CallHierarchyIncomingCall>?>(new Container<CallHierarchyIncomingCall>(incoming));
    }

    public override Task<Container<CallHierarchyOutgoingCall>?> Handle(CallHierarchyOutgoingCallsParams request, CancellationToken cancellationToken)
    {
        // The item's own DocumentUri, and ResolveForQuery so a file that is not open still answers.
        // See the incoming handler above for both.
        SymbolQueryContext? target = _support.ResolveForQuery(request.Item.Uri, cancellationToken);
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

    /// <summary>One caller entry: the file, the function inside it that makes the calls, and where.</summary>
    internal readonly record struct IncomingGroup(ScriptRecord Record, FunctionSymbol? Caller, List<LspRange> Ranges);

    /// <summary>
    /// Groups a function's call sites by the function that CONTAINS each one, not by the file it
    /// sits in.
    ///
    /// Grouping by file alone produced ONE entry per file, named after whichever function held the
    /// first range, with every other function's call sites hung off it — so a file where both a()
    /// and b() call the target reported a single caller a(), and clicking b()'s range under it
    /// jumped into a body that does not contain it. The protocol models one entry per calling
    /// FUNCTION, which is also what the UI draws its tree from.
    ///
    /// Keyed by the caller's declaration position rather than by its name: a name is not unique
    /// across a file's classes, and the position is what the item is about to point at anyway.
    ///
    /// A pure static so the decision is testable without protocol objects, for the same reason
    /// <c>WorkspaceFoldersHandler.NextFolderSet</c> is one.
    /// </summary>
    internal static List<IncomingGroup> GroupIncomingCalls(
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> references)
    {
        Dictionary<string, IncomingGroup> byCaller = new(StringComparer.Ordinal);
        foreach ( (ScriptRecord record, ReferenceEntry entry) in references )
        {
            if ( entry.Kind == ReferenceKind.Definition )
            {
                continue;
            }

            FunctionSymbol? caller = ContainingFunction(record, entry.Range.Start);
            string groupKey = caller is null
                ? record.Path
                : $"{record.Path}\u0000{caller.NameRange.Start.Line}:{caller.NameRange.Start.Character}";

            if ( !byCaller.TryGetValue(groupKey, out IncomingGroup group) )
            {
                group = new IncomingGroup(record, caller, []);
                byCaller[groupKey] = group;
            }

            group.Ranges.Add(entry.Range.ToLsp());
        }

        return [.. byCaller.Values];
    }

    private static FunctionSymbol? ContainingFunction(ScriptRecord record, Position start)
    {
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
