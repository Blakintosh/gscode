using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Workspace.Database;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using SymbolKind = GSCode.Core.Symbols.SymbolKind;
using Position = GSCode.Core.Text.Position;
using TextRange = GSCode.Core.Text.TextRange;

namespace GSCode.Server.Handlers;

/// <summary>
/// Go-to-implementation for a class method: the methods that OVERRIDE it, in the classes that
/// inherit from the one declaring it.
///
/// The question definition cannot answer. A call written inside a base class runs the subclass's
/// version, and go-to-definition — correctly — lands on the one the call resolves to statically.
/// Which classes redefine it from there is what the class graph knows and the call site does not.
///
/// A FIELD asks the same question in the other dialect GSC has for it. A field is declared
/// nowhere and holds whatever its writes give it, so what a field IS, is its plain assignments:
/// every <c>level.foo = …</c> in the workspace is one answer to "what is in here".
///
/// PLAIN assignments only. A compound update — <c>level.count += 1</c> — never establishes what
/// the field is; it adjusts a value some plain assignment already decided, so listing it shows a
/// reader a step rather than an answer. That is the line between this request and
/// go-to-definition, which lists every write because it asks where the field is SET rather than
/// what it is. Extraction draws the same line one layer down, as
/// <see cref="ReferenceKind.FieldUpdate"/> explains.
///
/// A write that binds a FUNCTION answers twice: the assignment line and the function it names.
/// <c>level.callback = &amp;on_damage;</c> is GSC's other spelling of an override — a reader at
/// <c>level thread [[ level.callback ]]();</c> has no way to reach <c>on_damage</c> otherwise,
/// since the arrow is absent and there is no class graph to walk — and the declaration is the
/// destination worth having. The assignment comes too rather than instead, because which game
/// mode installed the callback is the other half of the answer.
///
/// A top-level function still returns nothing rather than falling back to its own declaration:
/// it has no overrides, the editor already has go-to-definition bound beside this, and answering
/// a different question than the one asked hides the fact that the feature did not apply.
/// </summary>
public sealed class ImplementationHandler : ImplementationHandlerBase
{
    /// <summary>
    /// The same bound <see cref="MethodResolution"/> walks ancestors under, for the same reason: a
    /// class graph with a cycle in it is a state the workspace can genuinely be in — it is what
    /// <c>ClassCycleLint</c> exists to report — and the walk has to survive one rather than assume
    /// the lint got there first. The visited set alone already stops a cycle; this bounds a chain
    /// that is merely absurd.
    /// </summary>
    private const int MaxDepth = 32;

    private readonly NavigationSupport _support;
    private readonly TextDocumentSelector _selector;

    public ImplementationHandler(NavigationSupport support, TextDocumentSelector selector)
    {
        _support = support;
        _selector = selector;
    }

    protected override ImplementationRegistrationOptions CreateRegistrationOptions(
        ImplementationCapability capability, ClientCapabilities clientCapabilities)
    {
        return new ImplementationRegistrationOptions { DocumentSelector = _selector };
    }

    public override Task<LocationOrLocationLinks?> Handle(ImplementationParams request, CancellationToken cancellationToken)
    {
        NavigationTarget? target = _support.Resolve(request.TextDocument.Uri, cancellationToken);
        if ( target is null )
        {
            return Task.FromResult<LocationOrLocationLinks?>(null);
        }

        PositionHit hit = _support.ResolveHit(target, request.Position.ToCore());
        if ( hit.Kind != HitKind.Reference )
        {
            return Task.FromResult<LocationOrLocationLinks?>(null);
        }

        // A class MEMBER answers the same way a field does. It DOES have a declaration, which is
        // go-to-definition's answer; what it IS, is still whatever its plain assignments give it.
        if ( hit.Key.Kind == SymbolKind.Field || hit.Key.Kind == SymbolKind.Member )
        {
            return Task.FromResult(Answer(FieldImplementations(target, hit.Key, cancellationToken)));
        }

        if ( hit.Key.Kind != SymbolKind.Function )
        {
            return Task.FromResult<LocationOrLocationLinks?>(null);
        }

        // Routed through MethodResolution rather than the function lookup, because that is what
        // says whether this name IS a method and which class owns it — a bare method call inside
        // the class writes no receiver, so the syntax at the cursor cannot tell.
        ImmutableArray<ResolvedFunction> resolved = MethodResolution.ResolveCall(
            target.Store, target.ContextId, target.Path, hit.Key, hit.ReferenceKind,
            askingNamespaces: target.Namespaces);

        List<Location> implementations = [];
        // A TUPLE, not a joined string. The set exists only to spot the same declaration reached
        // twice — a raw file and the mod overlay replacing it — and a joined key allocated a string
        // per candidate to answer that.
        HashSet<(string Path, Position At)> emitted = [];

        foreach ( ResolvedFunction function in resolved )
        {
            if ( function.OwnerClass is null )
            {
                continue;
            }

            CollectOverrides(target, function.OwnerClass.KeyName, function.Function.KeyName, implementations, emitted);
        }

        return Task.FromResult(Answer(implementations));
    }

    /// <summary>
    /// What a field IS: every plain assignment to it, plus the declaration of any function one of
    /// them binds.
    ///
    /// Deduplicated by location through the same <c>emitted</c> set the override walk uses, and
    /// for a wider set of reasons here — a mod overlay shadowing a raw file, as there, but also
    /// two game modes binding one handler, which resolves to one declaration from two writes.
    /// </summary>
    private List<Location> FieldImplementations(
        NavigationTarget target, SymbolKey field, CancellationToken cancellationToken)
    {
        List<Location> implementations = [];
        HashSet<(string Path, Position At)> emitted = [];

        // ONE reference set, used for both halves. This ran the workspace query twice — once
        // here and once inside FieldTargets — which paid the indexed lookup, the shadow rule and
        // the include scoping over again, and left the writes and the bindings coming from two
        // sets nothing guaranteed were equal.
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> references =
            FieldTargets.ReferencesTo(_support, target, field);

        // The assignments themselves. FieldWrite alone, so a compound update is left out — see
        // the class comment.
        foreach ( (ScriptRecord record, ReferenceEntry entry) in references )
        {
            cancellationToken.ThrowIfCancellationRequested();

            if ( entry.Kind == ReferenceKind.FieldWrite && emitted.Add((record.Path, entry.Range.Start)) )
            {
                implementations.Add(LspMapping.LocationAt(record.Path, entry.Range));
            }
        }

        // And what any of them named. A function is reached in ONE hop this way; from the
        // assignment line alone the reader would have to ask again.
        foreach ( ResolvedFunction resolved in FieldTargets.FunctionsOf(target, references, field, cancellationToken) )
        {
            if ( emitted.Add((resolved.DeclaringPath, resolved.Function.NameRange.Start)) )
            {
                implementations.Add(LspMapping.LocationAt(resolved.DeclaringPath, resolved.Function.NameRange));
            }
        }

        return implementations;
    }

    /// <summary>Null for an empty list — the protocol's way of saying the request did not apply.</summary>
    private static LocationOrLocationLinks? Answer(List<Location> locations)
    {
        if ( locations.Count == 0 )
        {
            return null;
        }

        return new LocationOrLocationLinks(locations.Select(location => new LocationOrLocationLink(location)));
    }

    /// <summary>
    /// Every descendant of <paramref name="classKeyName"/> that declares a method called
    /// <paramref name="methodKeyName"/>, depth-first.
    ///
    /// Descendants rather than direct children: an override two levels down is still an override,
    /// and the class it sits in may not redeclare anything else. A class that does NOT redeclare
    /// the name contributes nothing itself and is still descended through, since its own children
    /// may.
    /// </summary>
    private void CollectOverrides(
        NavigationTarget target,
        string classKeyName,
        string methodKeyName,
        List<Location> into,
        HashSet<(string Path, Position At)> emitted)
    {
        Queue<(string ClassKeyName, int Depth)> pending = new();
        HashSet<string> visited = new(StringComparer.Ordinal) { classKeyName };
        pending.Enqueue((classKeyName, 0));

        while ( pending.Count > 0 )
        {
            (string current, int depth) = pending.Dequeue();
            if ( depth >= MaxDepth )
            {
                continue;
            }

            foreach ( string childKeyName in target.Store.Classes.DirectChildren(current) )
            {
                if ( !visited.Add(childKeyName) )
                {
                    continue;
                }

                pending.Enqueue((childKeyName, depth + 1));

                foreach ( ResolvedClass child in DatabaseQueries.LookupClasses(
                    target.Store, target.ContextId, namespaceName: null, childKeyName) )
                {
                    AddOverride(child, methodKeyName, into, emitted);
                }
            }
        }
    }

    private static void AddOverride(
        ResolvedClass child, string methodKeyName, List<Location> into, HashSet<(string Path, Position At)> emitted)
    {
        foreach ( FunctionSymbol method in child.Class.Methods )
        {
            if ( !string.Equals(method.KeyName, methodKeyName, StringComparison.Ordinal) )
            {
                continue;
            }

            // The file the method's range is truly a position in, by the rule
            // ResolvedFunction.DeclaringPath states: a class pulled in through an #insert carries
            // header-true ranges, and its methods carry their own.
            string path = method.SourceFile.Length > 0 ? method.SourceFile : child.DeclaringPath;
            TextRange range = method.NameRange;

            // A class the store holds twice — a raw file and the mod overlay replacing it — would
            // otherwise be offered as two identical jumps.
            if ( emitted.Add((path, range.Start)) )
            {
                into.Add(LspMapping.LocationAt(path, range));
            }
        }
    }
}
