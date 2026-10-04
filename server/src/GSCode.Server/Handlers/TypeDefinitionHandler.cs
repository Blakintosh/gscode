using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Typing;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using SymbolKind = GSCode.Core.Symbols.SymbolKind;

namespace GSCode.Server.Handlers;

/// <summary>
/// Go-to-type-definition for a local: the declaration of what the variable HOLDS, where
/// go-to-definition gives the line that assigned it.
///
/// GSC declares no types, so the answer comes from flow typing rather than from syntax — and only
/// where that inference carries an identity, not merely a type. Two values do:
///
/// * a class instance, which <see cref="FlowTyper"/> records as <c>ScrValue.InstanceClass</c> and
///   produces at exactly one place, a <c>new Foo()</c> expression — directly, or carried forward
///   through the environment to wherever the variable is read;
/// * a function pointer, recorded as <c>ScrValue.FunctionTarget</c> from <c>&amp;foo</c>, a bare
///   qualified name, or a dereference of another pointer.
///
/// A FIELD carries the same two identities and is answered the same way, but not from the flow
/// pass: <c>FlowTyper.TryGetValueAt</c> resolves through <c>AstSearch.TryFindLocalContext</c>, so a
/// field is invisible to it, and the writes that would answer are usually in another file
/// altogether. <see cref="FieldTargets"/> reads them out of the index instead. The two questions
/// are otherwise the same one, and the same two declaration lookups finish them.
///
/// An ENTITY still answers nothing, and that is worth stating because it looks like an omission
/// and is not: a <c>player</c> is an engine type described by the bundled object-field data and
/// declared in no script, so there is nowhere to jump to.
/// </summary>
public sealed class TypeDefinitionHandler : TypeDefinitionHandlerBase
{
    private readonly NavigationSupport _support;
    private readonly BuiltinApiSet _builtins;
    private readonly ObjectFields _objectFields;
    private readonly TextDocumentSelector _selector;

    public TypeDefinitionHandler(
        NavigationSupport support, BuiltinApiSet builtins, ObjectFields objectFields, TextDocumentSelector selector)
    {
        _support = support;
        _builtins = builtins;
        _objectFields = objectFields;
        _selector = selector;
    }

    protected override TypeDefinitionRegistrationOptions CreateRegistrationOptions(
        TypeDefinitionCapability capability, ClientCapabilities clientCapabilities)
    {
        return new TypeDefinitionRegistrationOptions { DocumentSelector = _selector };
    }

    public override Task<LocationOrLocationLinks?> Handle(TypeDefinitionParams request, CancellationToken cancellationToken)
    {
        NavigationTarget? target = _support.Resolve(request.TextDocument.Uri, cancellationToken);
        if ( target is null )
        {
            return Task.FromResult<LocationOrLocationLinks?>(null);
        }

        PositionHit hit = _support.ResolveHit(target, request.Position.ToCore());
        List<Location> locations = hit.Key.Kind == SymbolKind.Field
            ? BoundDeclarations(target, hit.Key, cancellationToken)
            : LocalDeclarations(target, request.Position.ToCore());

        if ( locations.Count == 0 )
        {
            return Task.FromResult<LocationOrLocationLinks?>(null);
        }

        return Task.FromResult<LocationOrLocationLinks?>(
            new LocationOrLocationLinks(locations.Select(location => new LocationOrLocationLink(location))));
    }

    /// <summary>
    /// What the LOCAL under the cursor holds, from the flow pass — a class instance or a function
    /// pointer, and nothing otherwise.
    /// </summary>
    private List<Location> LocalDeclarations(NavigationTarget target, GSCode.Core.Text.Position position)
    {
        FlowTyper typer = new(_builtins.For(target.Language), _objectFields);
        if ( !typer.TryGetValueAt(target.Result, position, out ScrValue value) )
        {
            return [];
        }

        List<Location> locations = ClassDeclarations(target, value);
        if ( locations.Count == 0 )
        {
            locations = FunctionDeclarations(target, value);
        }

        return locations;
    }

    /// <summary>
    /// What the FIELD under the cursor holds, from the writes the index recorded for it.
    ///
    /// Classes first and functions only if there are none, matching the local path exactly: a
    /// field written with both a <c>new Foo()</c> somewhere and an <c>&amp;bar</c> somewhere else
    /// is a field the scripts use for two things, and answering with the instance is the same
    /// choice the flow pass makes when a value could be read either way.
    /// </summary>
    private List<Location> BoundDeclarations(NavigationTarget target, SymbolKey field, CancellationToken cancellationToken)
    {
        // One reference set for both questions below, since they ask it of the same field.
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> references =
            FieldTargets.ReferencesTo(_support, target, field);

        List<Location> locations = [];

        foreach ( ResolvedClass resolved in FieldTargets.ClassesOf(target, references, field, cancellationToken) )
        {
            locations.Add(LspMapping.LocationAt(resolved.DeclaringPath, resolved.Class.NameRange));
        }

        if ( locations.Count > 0 )
        {
            return locations;
        }

        foreach ( ResolvedFunction resolved in FieldTargets.FunctionsOf(target, references, field, cancellationToken) )
        {
            locations.Add(LspMapping.LocationAt(resolved.DeclaringPath, resolved.Function.NameRange));
        }

        return locations;
    }

    /// <summary>
    /// The declaration of the class an instance value names, or none when the value is not one.
    ///
    /// <c>InstanceClass</c> holds the class name as the <c>new</c> expression WROTE it, and every
    /// lookup here is by the lowercase canonical name, so it is interned down first — the same step
    /// <see cref="TypeHierarchyHandler"/> takes with a name that arrived from the client.
    /// </summary>
    private static List<Location> ClassDeclarations(NavigationTarget target, ScrValue value)
    {
        List<Location> locations = [];
        if ( string.IsNullOrEmpty(value.InstanceClass) )
        {
            return locations;
        }

        string keyName = NameTable.Shared.InternLower(value.InstanceClass);
        foreach ( ResolvedClass resolved in DatabaseQueries.LookupClasses(
            target.Store, target.ContextId, namespaceName: null, keyName) )
        {
            locations.Add(LspMapping.LocationAt(resolved.DeclaringPath, resolved.Class.NameRange));
        }

        return locations;
    }

    /// <summary>
    /// The declaration of the function a pointer value holds, or none when the value is not one.
    /// <c>ScrFunctionRef.Namespace</c> is null for a pointer written unqualified, which
    /// <see cref="DatabaseQueries.LookupFunctions"/> already reads as "any namespace this file can
    /// reach".
    /// </summary>
    private static List<Location> FunctionDeclarations(NavigationTarget target, ScrValue value)
    {
        List<Location> locations = [];
        if ( value.FunctionTarget is not ScrFunctionRef pointer )
        {
            return locations;
        }

        string keyName = NameTable.Shared.InternLower(pointer.Name);
        ImmutableArray<ResolvedFunction> functions = DatabaseQueries.LookupFunctions(
            target.Store,
            target.ContextId,
            target.Path,
            pointer.Namespace,
            keyName,
            askingNamespaces: target.Namespaces);

        foreach ( ResolvedFunction function in functions )
        {
            locations.Add(LspMapping.LocationAt(function.DeclaringPath, function.Function.NameRange));
        }

        return locations;
    }
}
