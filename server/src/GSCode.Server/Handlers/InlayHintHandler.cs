using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Typing;
using GSCode.Server.Configuration;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

// The implicit string -> InlayHint.Label conversion is nullable-annotated, so assigning a
// non-null string trips CS8601; suppressed for this file (the values are always non-null).
#pragma warning disable CS8601

namespace GSCode.Server.Handlers;

/// <summary>
/// Inlay hints: inferred local types after assignments (FlowTyper) and parameter names
/// before call arguments. Each family is independently toggleable and only shown when the
/// underlying fact is certain.
/// </summary>
public sealed class InlayHintHandler : InlayHintsHandlerBase
{
    private readonly NavigationSupport _support;
    private readonly BuiltinApiSet _builtins;
    private readonly ObjectFields _objectFields;
    private readonly ServerSettings _settings;
    private readonly TextDocumentSelector _selector;

    public InlayHintHandler(NavigationSupport support, BuiltinApiSet builtins, ObjectFields objectFields, ServerSettings settings, TextDocumentSelector selector)
    {
        _support = support;
        _builtins = builtins;
        _objectFields = objectFields;
        _settings = settings;
        _selector = selector;
    }

    protected override InlayHintRegistrationOptions CreateRegistrationOptions(InlayHintClientCapabilities capability, ClientCapabilities clientCapabilities)
    {
        return new InlayHintRegistrationOptions { DocumentSelector = _selector, ResolveProvider = false };
    }

    // Hints are complete up front (ResolveProvider = false), so resolve is a passthrough.
    public override Task<InlayHint> Handle(InlayHint request, CancellationToken cancellationToken)
    {
        return Task.FromResult(request);
    }

    public override Task<InlayHintContainer?> Handle(InlayHintParams request, CancellationToken cancellationToken)
    {
        NavigationTarget? target = _support.Resolve(request.TextDocument.Uri);
        if ( target is null )
        {
            return Task.FromResult<InlayHintContainer?>(null);
        }

        TextRange window = request.Range.ToCore();
        List<InlayHint> hints = [];

        if ( _settings.InlayInferredTypes )
        {
            FlowTyper typer = new(_builtins.For(target.Language), _objectFields);
            foreach ( InferredAssignment inferred in typer.InferAssignments(target.Result) )
            {
                // First assignment only: a `: int` label repeated at every reassignment is noise.
                // The list itself carries them all, because hover needs the later ones.
                if ( inferred.IsFirstForName && window.Contains(inferred.NameRange.Start) )
                {
                    hints.Add(new InlayHint
                    {
                        Position = inferred.NameRange.End.ToLsp(),
                        Label = ": " + inferred.Type.DisplayName(),
                        Kind = InlayHintKind.Type,
                        PaddingLeft = false,
                    });
                }
            }
        }

        if ( _settings.InlayParameterNames )
        {
            AddParameterNameHints(target, window, hints);
        }

        return Task.FromResult<InlayHintContainer?>(new InlayHintContainer(hints));
    }

    /// <summary>True when the call's callee token was produced by expanding a macro body.</summary>
    private static bool IsFromMacroExpansion(CallNode call)
    {
        switch ( call.Callee )
        {
            case IdentifierNode identifier:
                return identifier.Token.Provenance.DefinitionSite is not null;
            case QualifiedNode qualified:
                return qualified.NameToken.Provenance.DefinitionSite is not null;
            default:
                return false;
        }
    }

    private void AddParameterNameHints(NavigationTarget target, TextRange window, List<InlayHint> hints)
    {
        foreach ( CallNode call in CollectCalls(target.Result.Tree.Root) )
        {
            if ( call.Arguments.Length == 0 || !window.Contains(call.Range.Start) )
            {
                continue;
            }

            // A call inside a macro body reports the INVOCATION's range, so hinting it would
            // stamp the whole expansion's parameter names onto the one call site.
            if ( IsFromMacroExpansion(call) )
            {
                continue;
            }

            ImmutableArray<string> parameters = ResolveParameterNames(target, call);
            if ( parameters.IsDefaultOrEmpty )
            {
                continue;
            }

            int count = Math.Min(parameters.Length, call.Arguments.Length);
            for ( int index = 0; index < count; index++ )
            {
                hints.Add(new InlayHint
                {
                    Position = call.Arguments[index].Range.Start.ToLsp(),
                    Label = parameters[index] + ":",
                    Kind = InlayHintKind.Parameter,
                    PaddingRight = true,
                });
            }
        }
    }

    private ImmutableArray<string> ResolveParameterNames(NavigationTarget target, CallNode call)
    {
        if ( call.Callee is IdentifierNode identifier )
        {
            // Inside a class body a bare name is a method first — so this has to be asked before the
            // namespace and builtin lookups below, or an inherited method's hints come out as some
            // unrelated engine function's parameter names.
            string? enclosingClass = EnclosingClassAt(target, call.Range.Start);
            if ( enclosingClass is not null )
            {
                ImmutableArray<string> method = MethodParameterNames(
                    target, new SymbolKey(null, identifier.Token.Text.ToLowerInvariant(), GSCode.Core.Symbols.SymbolKind.Function, enclosingClass),
                    ReferenceKind.Call);

                if ( !method.IsDefault )
                {
                    return method;
                }
            }

            // A script function in one of the file's namespaces, else a builtin. The declared set,
            // not the spans — a phantom span cost a full store scan here on every hint.
            foreach ( string declared in target.Result.Extraction.DeclaredNamespaces )
            {
                ImmutableArray<ResolvedFunction> found = DatabaseQueries.LookupFunctions(
                    target.Store, target.ContextId, target.Path, declared, identifier.Token.Text.ToLowerInvariant(), askingNamespaces: target.Namespaces);
                if ( found.Length > 0 )
                {
                    return [.. found[0].Function.Parameters.Select(static p => p.Name)];
                }
            }

            BuiltinFunction? builtin = _builtins.For(target.Language).Find(identifier.Token.Text);
            if ( builtin is not null && builtin.Overloads.Length > 0 )
            {
                return [.. builtin.Overloads[0].Parameters.Select(static p => p.Name)];
            }

            return default;
        }

        if ( call.Callee is QualifiedNode qualified )
        {
            ImmutableArray<ResolvedFunction> found = DatabaseQueries.LookupFunctions(
                target.Store, target.ContextId, target.Path,
                qualified.NamespaceToken.Text.ToLowerInvariant(),
                qualified.NameToken.Text.ToLowerInvariant(), askingNamespaces: target.Namespaces);
            if ( found.Length > 0 )
            {
                return [.. found[0].Function.Parameters.Select(static p => p.Name)];
            }

            // The qualifier may name a CLASS rather than a namespace — Class::method(). Tried second
            // so a name that is both, which BO3 ships, keeps meaning the namespace.
            return MethodParameterNames(
                target,
                new SymbolKey(
                    qualified.NamespaceToken.Text.ToLowerInvariant(),
                    qualified.NameToken.Text.ToLowerInvariant(),
                    GSCode.Core.Symbols.SymbolKind.Function),
                ReferenceKind.Call);
        }

        return default;
    }

    /// <summary>
    /// Parameter names of the method a key resolves to, or default when it resolves to none. Kept
    /// separate from the function path because a method is reached through the class chain rather
    /// than by namespace, and <see cref="DatabaseQueries.LookupFunctions"/> cannot see one at all.
    /// </summary>
    private static ImmutableArray<string> MethodParameterNames(
        NavigationTarget target, SymbolKey written, ReferenceKind referenceKind)
    {
        SymbolKey canonical = MethodResolution.Canonicalize(
            target.Store, target.ContextId, written, referenceKind);

        if ( canonical.OwnerClass is null )
        {
            return default;
        }

        ImmutableArray<ResolvedFunction> methods = MethodResolution.LookupMethods(
            target.Store, target.ContextId, canonical.OwnerClass, canonical.Name);

        if ( methods.Length == 0 )
        {
            return default;
        }

        return [.. methods[0].Function.Parameters.Select(static p => p.Name)];
    }

    /// <summary>The class whose body contains this position, over the file's own handful of classes.</summary>
    private static string? EnclosingClassAt(NavigationTarget target, GSCode.Core.Text.Position position)
    {
        foreach ( ClassSymbol classSymbol in target.Result.Extraction.Classes )
        {
            if ( classSymbol.FullRange.Contains(position) )
            {
                return classSymbol.KeyName;
            }
        }

        return null;
    }

    private static IEnumerable<CallNode> CollectCalls(AstNode root)
    {
        Stack<AstNode> stack = new();
        stack.Push(root);

        while ( stack.Count > 0 )
        {
            AstNode node = stack.Pop();
            if ( node is CallNode call )
            {
                yield return call;
            }

            foreach ( AstNode child in AstSearch.ChildrenOf(node) )
            {
                stack.Push(child);
            }
        }
    }
}
