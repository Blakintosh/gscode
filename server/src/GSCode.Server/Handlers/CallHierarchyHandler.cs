using System.Collections.Immutable;
using GSCode.Core;
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
///
/// A callback FIELD prepares on the functions bound to it — <c>level.callback = &amp;on_damage</c>
/// anchors on <c>on_damage</c>. A field is not callable and never will be; what is callable is
/// what was put in it, and <c>level thread [[ level.callback ]]();</c> is how a GSC script spells
/// the indirect call whose target the hierarchy exists to trace. Only the anchoring step knows
/// about fields: an item is a function either way, so incoming and outgoing are unchanged.
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
        if ( hit.Kind != HitKind.Reference )
        {
            return Task.FromResult<Container<CallHierarchyItem>?>(null);
        }

        if ( hit.Key.Kind == SymbolKind.Field )
        {
            List<CallHierarchyItem> bound = [];
            // The two-level form, where the other three field callers use the flattened
            // FunctionsOf: the item's Data carries the KEY, and the key a write named is not the
            // one rebuilt from the declaration it resolved to. An unqualified `&foo` keys with a
            // null namespace, which is what the union behind find-references is built on.
            foreach ( SymbolKey held in FieldTargets.FunctionKeysOf(_support, target, hit.Key, cancellationToken) )
            {
                foreach ( ResolvedFunction resolved in FieldTargets.Functions(target, held) )
                {
                    bound.Add(MakeItem(held, resolved.Record, resolved.Function.NameRange));
                }
            }

            return Task.FromResult<Container<CallHierarchyItem>?>(
                bound.Count > 0 ? new Container<CallHierarchyItem>(bound) : null);
        }

        if ( hit.Key.Kind != SymbolKind.Function )
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
            //
            CallHierarchyItem item = group.Caller is not null
                ? MakeItem(CallerKey(group.Caller), group.Record, group.Caller.NameRange.ToLsp())
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
        ImmutableArray<ResolvedFunction> functions = Declarations(target, key.Value, ReferenceKind.Call, out SymbolKey _);
        if ( functions.Length == 0 )
        {
            return Task.FromResult<Container<CallHierarchyOutgoingCall>?>(null);
        }

        ResolvedFunction self = functions[0];
        Dictionary<OutgoingSite, List<LspRange>> calls = new();
        foreach ( ReferenceEntry entry in self.Record.References )
        {
            // !entry.FromMacro: an expanded call is keyed to its INVOCATION range, so a macro used
            // twice in one function would list the same outgoing edge at ranges that spell the
            // macro's name, not the callee's.
            //
            // A MethodCall is the `[[ x ]]->m()` arrow form, which is how one method calls another.
            bool isCall = entry.Kind == ReferenceKind.Call || entry.Kind == ReferenceKind.MethodCall;
            if ( isCall && !entry.FromMacro && entry.Key.Kind == SymbolKind.Function
                && self.Function.FullRange.Contains(entry.Range.Start) )
            {
                OutgoingSite site = new(entry.Key, entry.Kind);
                if ( !calls.TryGetValue(site, out List<LspRange>? ranges) )
                {
                    ranges = [];
                    calls[site] = ranges;
                }

                ranges.Add(entry.Range.ToLsp());
            }
        }

        List<CallHierarchyOutgoingCall> outgoing = [];
        foreach ( KeyValuePair<OutgoingSite, List<LspRange>> call in calls )
        {
            ImmutableArray<ResolvedFunction> resolved = Declarations(target, call.Key.Key, call.Key.Kind, out SymbolKey callee);
            if ( resolved.Length == 0 )
            {
                continue;
            }

            List<LspRange> ranges = call.Value;
            CallHierarchyItem item = MakeItem(callee, resolved[0].Record, resolved[0].Function.NameRange.ToLsp());
            outgoing.Add(new CallHierarchyOutgoingCall { To = item, FromRanges = new Container<LspRange>(ranges) });
        }

        return Task.FromResult<Container<CallHierarchyOutgoingCall>?>(new Container<CallHierarchyOutgoingCall>(outgoing));
    }

    /// <summary>
    /// The key a calling function's own callers are indexed under, which is what expanding its item
    /// asks for. A method is keyed by its owner class. A function is keyed on its KEY namespace, not
    /// its declared one: a merge dialect declares a function into its file stem but keys every call
    /// to it with no namespace, so the declared one matched no reference.
    /// </summary>
    private static SymbolKey CallerKey(FunctionSymbol caller)
    {
        if ( caller.OwnerClassKeyName is string ownerClass )
        {
            return new SymbolKey(null, caller.KeyName, SymbolKind.Function, ownerClass);
        }

        return new SymbolKey(GameProfile.Active.KeyNamespace(caller.Namespace), caller.KeyName, SymbolKind.Function);
    }

    /// <summary>One distinct callee inside a body: the key as written, and how it was called.</summary>
    private readonly record struct OutgoingSite(SymbolKey Key, ReferenceKind Kind);

    /// <summary>
    /// The declarations a function or method key names, and the key an item for them should carry.
    ///
    /// A method goes through <see cref="MethodResolution"/> — canonicalized to the class that
    /// declares it, then looked up there — because the function lookup knows nothing of classes and
    /// answered every method with nothing, which left a method's outgoing calls, and every method it
    /// called, off the tree. An arrow call on a receiver whose class is unknown is keyed with no
    /// owner, the same as an unqualified call; the kind is what tells the two apart, so it is passed
    /// to the canonicalization rather than guessed from the key. The canonical key is handed back so
    /// the callee's own item names the declaring class and expands like any other.
    /// </summary>
    private static ImmutableArray<ResolvedFunction> Declarations(
        SymbolQueryContext target, SymbolKey key, ReferenceKind kind, out SymbolKey canonical)
    {
        if ( key.OwnerClass is null && kind != ReferenceKind.MethodCall )
        {
            canonical = key;
            return DatabaseQueries.LookupFunctions(
                target.Store, target.ContextId, target.Path, key.Namespace, key.Name, askingNamespaces: target.Namespaces);
        }

        canonical = MethodResolution.Canonicalize(target.Store, target.ContextId, key, kind);
        if ( canonical.OwnerClass is null )
        {
            return [];
        }

        return MethodResolution.LookupMethods(target.Store, target.ContextId, canonical.OwnerClass, canonical.Name);
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
        // Keyed by a TUPLE rather than a joined string. This runs once per reference, and on a
        // stock utility that is tens of thousands of them, each allocating a key the dictionary
        // keeps only in order to compare it. A caller nothing contains groups by path alone, which
        // the default position stands in for — no real declaration's name starts before (0,0).
        Dictionary<(string Path, Position Caller), IncomingGroup> byCaller = [];
        foreach ( (ScriptRecord record, ReferenceEntry entry) in references )
        {
            if ( entry.Kind == ReferenceKind.Definition )
            {
                continue;
            }

            FunctionSymbol? caller = EnclosingFunction.At(record.Functions, record.Classes, entry.Range.Start);
            (string Path, Position Caller) groupKey = caller is null
                ? (record.Path, default)
                : (record.Path, caller.NameRange.Start);

            if ( !byCaller.TryGetValue(groupKey, out IncomingGroup group) )
            {
                group = new IncomingGroup(record, caller, []);
                byCaller[groupKey] = group;
            }

            group.Ranges.Add(entry.Range.ToLsp());
        }

        return [.. byCaller.Values];
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
            // Everything the key needs has to survive the round trip through the client: without the
            // owner, a method's item comes back as a free function of the same name, which nothing
            // calls.
            Data = JToken.FromObject(new { ns = key.Namespace ?? "", name = key.Name, owner = key.OwnerClass ?? "" }),
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
        string owner = item.Data["owner"]?.ToString() ?? "";
        if ( name.Length == 0 )
        {
            return null;
        }

        return new SymbolKey(ns.Length > 0 ? ns : null, name, SymbolKind.Function, owner.Length > 0 ? owner : null);
    }
}
