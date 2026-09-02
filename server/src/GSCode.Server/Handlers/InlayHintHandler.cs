using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser.Preprocessing;
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
/// Inlay hints: inferred local types after assignments (FlowTyper), parameter names before
/// call arguments, and macro parameter names before the arguments of a #define invocation.
/// Each family is independently toggleable and only shown when the underlying fact is certain.
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
        // ResolveFresh for the same reason CodeLens uses it: hints are positional, and stale
        // analysis painted them one edit behind the buffer.
        NavigationTarget? target = _support.ResolveFresh(request.TextDocument.Uri);
        if ( target is null )
        {
            return Task.FromResult<InlayHintContainer?>(null);
        }

        TextRange window = request.Range.ToCore();
        List<InlayHint> hints = [];

        // Per-expression values are needed only by the parameter-name pass, which has to ask what a
        // `[[ ptr ]]` holds to know whose parameters to name. The type-hint pass wants assignment
        // sites alone, so it runs the cheaper walk that records nothing else. The macro pass reads
        // the preprocessor's invocation list and needs neither, so it pays for no flow analysis.
        ScriptTypes types = ScriptTypes.Empty;
        ImmutableArray<InferredAssignment> assignments = [];

        if ( _settings.InlayInferredTypes || _settings.InlayParameterNames )
        {
            FlowTyper typer = new(_builtins.For(target.Language), _objectFields);

            if ( _settings.InlayParameterNames )
            {
                types = typer.InferValues(target.Result);
                assignments = types.Assignments;
            }
            else
            {
                assignments = typer.InferAssignments(target.Result);
            }
        }

        if ( _settings.InlayInferredTypes )
        {
            foreach ( InferredAssignment inferred in assignments )
            {
                // First assignment only: a `: int` label repeated at every reassignment is noise.
                // The list itself carries them all, because hover needs the later ones.
                if ( inferred.IsFirstForName && window.Contains(inferred.NameRange.Start) )
                {
                    hints.Add(new InlayHint
                    {
                        Position = inferred.NameRange.End.ToLsp(),
                        Label = ": " + inferred.Display,
                        Kind = InlayHintKind.Type,
                        PaddingLeft = false,
                    });
                }
            }
        }

        if ( _settings.InlayParameterNames )
        {
            AddParameterNameHints(target, types, window, hints);
        }

        if ( _settings.InlayMacroParameterNames )
        {
            AddMacroParameterNameHints(target, window, hints);
        }

        return Task.FromResult<InlayHintContainer?>(new InlayHintContainer(hints));
    }

    /// <summary>
    /// Parameter names before the arguments of a MACRO invocation — <c>IS_TRUE( __a: value )</c>.
    ///
    /// A separate pass from <see cref="AddParameterNameHints"/> rather than a relaxation of its
    /// macro guard, because by the time there is a tree the invocation is gone: the call the
    /// author wrote was replaced by the body it expands to, and every token of that body reports
    /// the invocation's own range. The preprocessor's invocation list is the only record that the
    /// call site existed, and it names the macro that was expanded there.
    ///
    /// Off by default (<c>inlayHints.macroParameterNames</c>). A macro parameter is named for the
    /// macro's implementation rather than for its caller — <c>__a</c>, <c>__b</c> — so unlike a
    /// function's parameters the name is often worth less than the space it takes.
    /// </summary>
    private static void AddMacroParameterNameHints(NavigationTarget target, TextRange window, List<InlayHint> hints)
    {
        string text = target.Result.Text.Text;

        foreach ( MacroInvocation invocation in target.Result.Preprocessed.MacroInvocations )
        {
            // Only invocations written in THIS file: one reached through an #insert has its range
            // in the header's coordinates, which here would land on unrelated lines.
            if ( invocation.SourceFile is not null || !window.Contains(invocation.Range.Start) )
            {
                continue;
            }

            // Object-like macros take no arguments, so there is nothing to label.
            if ( invocation.Definition.Parameters is not { } parameters || parameters.IsEmpty )
            {
                continue;
            }

            // The range covers the NAME only — `IS_TRUE`, not `IS_TRUE( v )` — so the arguments
            // are found by scanning the text that follows it.
            int afterName = target.Result.Text.GetOffset(invocation.Range.End);
            if ( afterName <= 0 || afterName > text.Length )
            {
                continue;
            }

            ImmutableArray<MacroArgumentSpan> spans = MacroExpansionPreview.ArgumentSpansFollowing(text, afterName);

            // Whichever list is shorter: a half-written invocation should label what it has, and a
            // wrong-arity one should not name arguments the macro never declared.
            int count = Math.Min(parameters.Length, spans.Length);
            for ( int index = 0; index < count; index++ )
            {
                hints.Add(new InlayHint
                {
                    Position = target.Result.Text.GetPosition(spans[index].Start).ToLsp(),
                    Label = parameters[index] + ":",
                    Kind = InlayHintKind.Parameter,
                    PaddingRight = true,
                });
            }
        }
    }

    /// <summary>True when the call's callee token was produced by expanding a macro body.</summary>
    private static bool IsFromMacroExpansion(ExprNode node)
    {
        switch ( node )
        {
            case ArrowCallNode arrowCall:
                return arrowCall.MethodToken.Provenance.DefinitionSite is not null;
            case CallNode { Callee: IdentifierNode identifier }:
                return identifier.Token.Provenance.DefinitionSite is not null;
            case CallNode { Callee: QualifiedNode qualified }:
                return qualified.NameToken.Provenance.DefinitionSite is not null;
            case CallNode { Callee: PointerDerefNode { Pointer: IdentifierNode pointer } }:
                return pointer.Token.Provenance.DefinitionSite is not null;
            default:
                return false;
        }
    }

    private void AddParameterNameHints(NavigationTarget target, ScriptTypes types, TextRange window, List<InlayHint> hints)
    {
        foreach ( ExprNode node in CollectCalls(target.Result.Tree.Root) )
        {
            ImmutableArray<ExprNode> arguments = node switch
            {
                CallNode call => call.Arguments,
                ArrowCallNode arrowCall => arrowCall.Arguments,
                _ => [],
            };

            if ( arguments.Length == 0 || !window.Contains(node.Range.Start) )
            {
                continue;
            }

            // A call inside a macro body reports the INVOCATION's range, so hinting it would
            // stamp the whole expansion's parameter names onto the one call site.
            if ( IsFromMacroExpansion(node) )
            {
                continue;
            }

            ImmutableArray<string> parameters = ResolveParameterNames(target, types, node);
            if ( parameters.IsDefaultOrEmpty )
            {
                continue;
            }

            int count = Math.Min(parameters.Length, arguments.Length);
            for ( int index = 0; index < count; index++ )
            {
                hints.Add(new InlayHint
                {
                    Position = arguments[index].Range.Start.ToLsp(),
                    Label = parameters[index] + ":",
                    Kind = InlayHintKind.Parameter,
                    PaddingRight = true,
                });
            }
        }
    }

    /// <summary>
    /// Parameter names for one call site, whichever of the four callee forms it uses.
    ///
    /// The two indirect forms — <c>[[ ptr ]]( ... )</c> and <c>[[ obj ]]-&gt;method( ... )</c> — are
    /// answered from the flow pass rather than from the syntax, because the callee is a VALUE there
    /// and the syntax names a local. Both were silent before: a pointer call is how most of a Black
    /// Ops III script's dispatch is written, so that was the majority of calls in some files.
    /// </summary>
    private ImmutableArray<string> ResolveParameterNames(NavigationTarget target, ScriptTypes types, ExprNode node)
    {
        if ( node is ArrowCallNode arrowCall )
        {
            // The class comes from what the object HOLDS. A method name alone resolves to nothing:
            // methods are keyed by their declaring class, and several classes can declare one name.
            string? instanceClass = types.ValueOf(arrowCall.Object.Pointer).InstanceClass;
            if ( instanceClass is null )
            {
                return default;
            }

            return MethodParameterNames(
                target,
                new SymbolKey(null, arrowCall.MethodToken.Text.ToLowerInvariant(), GSCode.Core.Symbols.SymbolKind.Function, instanceClass.ToLowerInvariant()),
                ReferenceKind.Call);
        }

        if ( node is not CallNode call )
        {
            return default;
        }

        if ( call.Callee is PointerDerefNode deref )
        {
            ScrFunctionRef? pointer = types.ValueOf(deref).FunctionTarget;
            if ( pointer is not { } reference )
            {
                return default;
            }

            return reference.Namespace is null
                ? UnqualifiedParameterNames(target, reference.Name)
                : QualifiedParameterNames(target, reference.Namespace, reference.Name);
        }

        return ResolveNamedParameterNames(target, call);
    }

    private ImmutableArray<string> ResolveNamedParameterNames(NavigationTarget target, CallNode call)
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

            return UnqualifiedParameterNames(target, identifier.Token.Text);
        }

        if ( call.Callee is QualifiedNode qualified )
        {
            return QualifiedParameterNames(
                target, qualified.NamespaceToken.Text, qualified.NameToken.Text);
        }

        return default;
    }

    /// <summary>A bare name: a script function in one of the file's namespaces, else a builtin.</summary>
    private ImmutableArray<string> UnqualifiedParameterNames(NavigationTarget target, string name)
    {
        // The DECLARED namespace set, not the spans — a phantom span cost a full store scan here on
        // every hint.
        foreach ( string declared in target.Result.Extraction.DeclaredNamespaces )
        {
            ImmutableArray<ResolvedFunction> found = DatabaseQueries.LookupFunctions(
                target.Store, target.ContextId, target.Path, declared, name.ToLowerInvariant(), askingNamespaces: target.Namespaces);
            if ( found.Length > 0 )
            {
                return [.. found[0].Function.Parameters.Select(static p => p.Name)];
            }
        }

        BuiltinFunction? builtin = _builtins.For(target.Language).Find(name);
        if ( builtin is not null && builtin.Overloads.Length > 0 )
        {
            return [.. builtin.Overloads[0].Parameters.Select(static p => p.Name)];
        }

        return default;
    }

    /// <summary>A <c>ns::name</c> reference, where the qualifier may name a namespace or a class.</summary>
    private ImmutableArray<string> QualifiedParameterNames(NavigationTarget target, string qualifier, string name)
    {
        ImmutableArray<ResolvedFunction> found = DatabaseQueries.LookupFunctions(
            target.Store, target.ContextId, target.Path,
            qualifier.ToLowerInvariant(), name.ToLowerInvariant(), askingNamespaces: target.Namespaces);
        if ( found.Length > 0 )
        {
            return [.. found[0].Function.Parameters.Select(static p => p.Name)];
        }

        // The qualifier may name a CLASS rather than a namespace — Class::method(). Tried second
        // so a name that is both, which BO3 ships, keeps meaning the namespace.
        return MethodParameterNames(
            target,
            new SymbolKey(qualifier.ToLowerInvariant(), name.ToLowerInvariant(), GSCode.Core.Symbols.SymbolKind.Function),
            ReferenceKind.Call);
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

    /// <summary>Every call site in the tree — the four <c>CallNode</c> forms and arrow method calls.</summary>
    private static IEnumerable<ExprNode> CollectCalls(AstNode root)
    {
        Stack<AstNode> stack = new();
        stack.Push(root);

        while ( stack.Count > 0 )
        {
            AstNode node = stack.Pop();
            if ( node is CallNode or ArrowCallNode )
            {
                yield return (ExprNode)node;
            }

            foreach ( AstNode child in AstSearch.ChildrenOf(node) )
            {
                stack.Push(child);
            }
        }
    }
}
