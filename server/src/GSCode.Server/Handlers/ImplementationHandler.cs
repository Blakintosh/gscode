using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Workspace.Database;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using SymbolKind = GSCode.Core.Symbols.SymbolKind;
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
/// Anything that is not a method returns nothing rather than falling back to the declaration. A
/// top-level function has no overrides, and a handler that quietly answers a different question
/// than the one asked is worse than one that declines: the editor already has go-to-definition
/// bound beside this, and duplicating it there hides the fact that the feature did not apply.
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
        if ( hit.Kind != HitKind.Reference || hit.Key.Kind != SymbolKind.Function )
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
        HashSet<string> emitted = new(StringComparer.Ordinal);

        foreach ( ResolvedFunction function in resolved )
        {
            if ( function.OwnerClass is null )
            {
                continue;
            }

            CollectOverrides(target, function.OwnerClass.KeyName, function.Function.KeyName, implementations, emitted);
        }

        if ( implementations.Count == 0 )
        {
            return Task.FromResult<LocationOrLocationLinks?>(null);
        }

        return Task.FromResult<LocationOrLocationLinks?>(
            new LocationOrLocationLinks(implementations.Select(location => new LocationOrLocationLink(location))));
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
        HashSet<string> emitted)
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
        ResolvedClass child, string methodKeyName, List<Location> into, HashSet<string> emitted)
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
            if ( emitted.Add(path + "|" + range.Start.Line + ":" + range.Start.Character) )
            {
                into.Add(LspMapping.LocationAt(path, range));
            }
        }
    }
}
