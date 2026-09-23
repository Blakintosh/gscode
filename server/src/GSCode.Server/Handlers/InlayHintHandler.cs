using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
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
using GSCode.Core;
using Position = GSCode.Core.Text.Position;

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

    /// <summary>
    /// One flow-typing pass per document VERSION, not per request.
    ///
    /// The client sends one <c>inlayHint</c> request per visible range, so scrolling fires one per
    /// frame — and each used to build a fresh <see cref="FlowTyper"/> and re-walk the whole file,
    /// throwing away <c>FlowTyper.InferValues</c>'s OWN per-instance memoisation by discarding the
    /// instance that held it. Keyed by <see cref="ParseResult"/> reference rather than path+version:
    /// <c>AnalyzeIfStale</c> already guarantees an unchanged document hands back the SAME instance,
    /// which is the fact the memoisation this replaces relied on too. A ConditionalWeakTable needs
    /// no eviction — an entry is collectible the moment nothing else holds its ParseResult, which is
    /// when the document closes or is next edited.
    /// </summary>
    private readonly ConditionalWeakTable<ParseResult, ScriptTypes> _typesCache = new();

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
        NavigationTarget? target = _support.ResolveFresh(request.TextDocument.Uri, cancellationToken);
        if ( target is null )
        {
            return Task.FromResult<InlayHintContainer?>(null);
        }

        TextRange window = request.Range.ToCore();
        List<InlayHint> hints = [];

        // Shared by all three families: one label per position, whichever family got there first.
        // See AddHint for the two ways one position legitimately arrives twice.
        HashSet<(Position Position, string Label)> seen = [];

        // Both families that need the flow pass read the SAME cached ScriptTypes now — the
        // parameter-name pass for what a `[[ ptr ]]` holds, the type-hint pass for
        // `types.Assignments`, which InferValues computes as part of the same walk it memoises
        // (see _typesCache). The macro pass reads the preprocessor's invocation list and needs
        // neither, so it pays for no flow analysis at all.
        ScriptTypes types = ScriptTypes.Empty;
        ImmutableArray<InferredAssignment> assignments = [];

        if ( _settings.InlayInferredTypes || _settings.InlayParameterNames )
        {
            types = InferTypes(target);
            assignments = types.Assignments;
        }

        if ( _settings.InlayInferredTypes )
        {
            foreach ( InferredAssignment inferred in assignments )
            {
                cancellationToken.ThrowIfCancellationRequested();

                // First assignment only: a `: int` label repeated at every reassignment is noise.
                // The list itself carries them all, because hover needs the later ones.
                if ( inferred.IsFirstForName && window.Contains(inferred.NameRange.Start) )
                {
                    AddHint(hints, seen, inferred.NameRange.End, ": " + inferred.Display, InlayHintKind.Type);
                }
            }
        }

        if ( _settings.InlayParameterNames )
        {
            AddParameterNameHints(target, types, window, hints, seen, cancellationToken);
        }

        if ( _settings.InlayMacroParameterNames )
        {
            AddMacroParameterNameHints(target, window, hints, seen, cancellationToken);
        }

        return Task.FromResult<InlayHintContainer?>(new InlayHintContainer(hints));
    }

    /// <summary>
    /// Adds one hint unless an identical one is already there.
    ///
    /// Two different things can legitimately produce the same label at the same position, and the
    /// client draws one label per hint, so without this they stack on top of each other:
    ///
    /// A macro body that names a parameter twice splices the SAME argument tokens twice
    /// (<c>ExpandBody</c> does an <c>AddRange</c> of them, so they keep the call site's own
    /// provenance), and the parser builds a node per splice. <c>#define TWICE( __a ) __a; __a;</c>
    /// invoked as <c>TWICE( foo( x ) )</c> therefore yields two <c>foo( x )</c> calls at ONE range
    /// — and they are correctly not treated as expansion-born, since the author did write that call.
    ///
    /// A nested invocation inside a <c>#define</c> body is re-recorded on every expansion of the
    /// outer macro, so a macro used three times contributes three identical invocations.
    ///
    /// Neither is wrong upstream. Both are the same question here — is this label already on screen
    /// at this spot — so both are answered in one place rather than guarded at each producer.
    /// </summary>
    private static void AddHint(
        List<InlayHint> hints,
        HashSet<(Position Position, string Label)> seen,
        Position position,
        string label,
        InlayHintKind kind)
    {
        if ( !seen.Add((position, label)) )
        {
            return;
        }

        hints.Add(new InlayHint
        {
            Position = position.ToLsp(),
            Label = label,
            Kind = kind,
            PaddingLeft = false,

            // A `name:` label wants a space after it; a `: int` one is already spaced by its colon.
            PaddingRight = kind == InlayHintKind.Parameter,
        });
    }

    /// <summary>
    /// The cached flow-typing pass over a document, computing it once per <see cref="ParseResult"/>
    /// instance. See <see cref="_typesCache"/> for why a request-scoped cache is not enough.
    /// </summary>
    internal ScriptTypes InferTypes(NavigationTarget target)
    {
        if ( _typesCache.TryGetValue(target.Result, out ScriptTypes? cached) )
        {
            return cached;
        }

        ScriptTypes computed = new FlowTyper(_builtins.For(target.Language), _objectFields).InferValues(target.Result);

        // AddOrUpdate rather than Add: two requests for the same unchanged document can race this
        // miss (a scroll firing two ranges before either returns), and InferValues is pure, so the
        // race costs a duplicate computation rather than a wrong answer. Add would throw on the
        // loser instead.
        _typesCache.AddOrUpdate(target.Result, computed);
        return computed;
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
    private static void AddMacroParameterNameHints(
        NavigationTarget target,
        TextRange window,
        List<InlayHint> hints,
        HashSet<(Position Position, string Label)> seen,
        CancellationToken cancellationToken)
    {
        string text = target.Result.Text.Text;

        foreach ( MacroInvocation invocation in target.Result.Preprocessed.MacroInvocations )
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Only invocations written in THIS file: one reached through an #insert has its range
            // in the header's coordinates, which here would land on unrelated lines.
            if ( invocation.SourceFile is not null )
            {
                continue;
            }

            // The NAME may sit above the window while the arguments it labels are inside it, so the
            // only cheap rejection here is a name that starts after the window ends — its arguments
            // follow the name, so they cannot be inside either. Everything else is decided per
            // argument below, against the position the label actually goes at.
            if ( invocation.Range.Start >= window.End )
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
                Position position = target.Result.Text.GetPosition(spans[index].Start);
                if ( window.Contains(position) )
                {
                    AddHint(hints, seen, position, parameters[index] + ":", InlayHintKind.Parameter);
                }
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
            case CallNode { Callee: PathQualifiedNode path }:
                return path.NameToken.Provenance.DefinitionSite is not null;
            case CallNode { Callee: PointerDerefNode deref }:
                return DerefFromMacroExpansion(deref);
            default:
                return false;
        }
    }

    /// <summary>
    /// Whether a <c>[[ ... ]]()</c> callee was produced by expanding a macro body.
    ///
    /// The pointer is not always a bare identifier — <c>[[ self.callback ]]()</c> holds it in a
    /// field — and the shapes that were not listed answered "not from a macro", which let an
    /// expansion be hinted at the invocation's own range. Asking the pointer EXPRESSION rather than
    /// enumerating its forms inline is what keeps the answer right as the grammar grows.
    /// </summary>
    private static bool DerefFromMacroExpansion(PointerDerefNode deref)
    {
        return deref.Pointer switch
        {
            IdentifierNode identifier => identifier.Token.Provenance.DefinitionSite is not null,
            MemberNode member => member.NameToken.Provenance.DefinitionSite is not null,
            QualifiedNode qualified => qualified.NameToken.Provenance.DefinitionSite is not null,
            PathQualifiedNode path => path.NameToken.Provenance.DefinitionSite is not null,
            _ => false,
        };
    }

    private void AddParameterNameHints(
        NavigationTarget target,
        ScriptTypes types,
        TextRange window,
        List<InlayHint> hints,
        HashSet<(Position Position, string Label)> seen,
        CancellationToken cancellationToken)
    {
        // Per call site, because resolving one can run a store lookup per declared namespace. The
        // client sends one of these per visible range, so scrolling produces a request per frame
        // and cancels the ones it has scrolled past.
        foreach ( ExprNode node in CollectCalls(target.Result.Tree.Root) )
        {
            cancellationToken.ThrowIfCancellationRequested();

            ImmutableArray<ExprNode> arguments = node switch
            {
                CallNode call => call.Arguments,
                ArrowCallNode arrowCall => arrowCall.Arguments,
                _ => [],
            };

            // OVERLAPS, not "starts inside". A call's arguments can be on screen while its callee
            // is a line or two above — every multi-line argument list at the top of the viewport is
            // this — and testing the call's start dropped all of its labels until the user scrolled
            // up far enough to bring the name itself into the window. Which labels come out is then
            // decided per argument, against the position each label goes at.
            if ( arguments.Length == 0 || !window.Overlaps(node.Range) )
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
                Position position = arguments[index].Range.Start;
                if ( window.Contains(position) && !SaysItsOwnName(arguments[index], parameters[index]) )
                {
                    AddHint(hints, seen, position, parameters[index] + ":", InlayHintKind.Parameter);
                }
            }
        }
    }

    /// <summary>
    /// Whether the argument already spells the parameter's name, making the label say nothing.
    ///
    /// <c>give_weapon( player, weapon )</c> reading <c>give_weapon( player: player, weapon: weapon )</c>
    /// is noise, and GSC's habit of naming a local after the parameter it feeds makes it common.
    ///
    /// BARE IDENTIFIERS only. A field access whose last segment happens to match —
    /// <c>give_weapon( self.weapon )</c> against a <c>weapon</c> parameter — is not the same
    /// claim: the agreement there can be coincidence, and hiding the label would hide the one thing
    /// the reader could not already see.
    /// </summary>
    private static bool SaysItsOwnName(ExprNode argument, string parameterName)
    {
        return argument is IdentifierNode identifier
            && string.Equals(identifier.Token.Text, parameterName, StringComparison.OrdinalIgnoreCase);
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
                new SymbolKey(
                    null,
                    NameTable.Shared.InternLower(arrowCall.MethodToken.Text),
                    GSCode.Core.Symbols.SymbolKind.Function,
                    NameTable.Shared.InternLower(instanceClass)),
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
                    target, new SymbolKey(
                        null, NameTable.Shared.InternLower(identifier.Token.Text), GSCode.Core.Symbols.SymbolKind.Function, enclosingClass),
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

        if ( call.Callee is PathQualifiedNode path )
        {
            return PathQualifiedParameterNames(target, path);
        }

        return default;
    }

    /// <summary>A bare name: a script function the file's scope reaches, else a builtin.</summary>
    private ImmutableArray<string> UnqualifiedParameterNames(NavigationTarget target, string name)
    {
        // Interned once outside the loop below, not recomputed per namespace tried — a file
        // importing several namespaces was lowercasing the same name once per candidate.
        string keyName = NameTable.Shared.InternLower(name);

        if ( !GameProfile.Active.ResolvesByNamespace )
        {
            // A merge dialect (#include: CoD4/WaW/MW2/BO1) has no #namespace, so the extractor
            // defaults every function's namespace to its FILE NAME STEM — a fallback naming a
            // scope nobody wrote. Asking by declared namespace there answers only for the asking
            // file's OWN functions and misses everything #include brings in, which is how these
            // games reach another file at all. It looked like the family worked on CoD4 because
            // same-file calls did get labels; every cross-file one was silently unlabelled.
            //
            // SignatureEngine makes exactly this split for exactly this reason, and the two answer
            // one question about one call site, so they have to make it the same way.
            FunctionSymbol? included = DatabaseQueries.FunctionInIncludeScope(
                target.Store,
                target.ContextId,
                target.Path,
                DatabaseQueries.IncludedScriptPaths(target.Result),
                keyName);

            if ( included is not null )
            {
                return [.. included.Parameters.Select(static p => p.Name)];
            }
        }
        else
        {
            // The DECLARED namespace set, not the spans — a phantom span cost a full store scan
            // here on every hint.
            foreach ( string declared in target.Result.Extraction.DeclaredNamespaces )
            {
                ImmutableArray<ResolvedFunction> found = DatabaseQueries.LookupFunctions(
                    target.Store, target.ContextId, target.Path, declared, keyName, askingNamespaces: target.Namespaces);
                if ( found.Length > 0 )
                {
                    return [.. found[0].Function.Parameters.Select(static p => p.Name)];
                }
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
        // Interned once, not once per candidate below — the qualifier is tried as both a
        // namespace and a class name, and both ask the same lowercase form.
        string qualifierKey = NameTable.Shared.InternLower(qualifier);
        string nameKey = NameTable.Shared.InternLower(name);

        ImmutableArray<ResolvedFunction> found = DatabaseQueries.LookupFunctions(
            target.Store, target.ContextId, target.Path,
            qualifierKey, nameKey, askingNamespaces: target.Namespaces);
        if ( found.Length > 0 )
        {
            return [.. found[0].Function.Parameters.Select(static p => p.Name)];
        }

        // The qualifier may name a CLASS rather than a namespace — Class::method(). Tried second
        // so a name that is both, which BO3 ships, keeps meaning the namespace.
        return MethodParameterNames(
            target,
            new SymbolKey(qualifierKey, nameKey, GSCode.Core.Symbols.SymbolKind.Function),
            ReferenceKind.Call);
    }

    /// <summary>
    /// A <c>maps\_utility::set_ambient( ... )</c> reference - the Infinity Ward path form, which
    /// only the merge dialects have.
    ///
    /// The path names the FILE the function is in rather than a namespace, so this is a lookup by
    /// name scoped to that one file. Asked with an EMPTY asking path on purpose: the scope helper
    /// searches the asking file first otherwise, and a path call names where it wants to go.
    ///
    /// These were silent, and on these games that is most cross-file calls - CoD4's shipped scripts
    /// write <c>maps\_utility::createOneshotEffect</c> alone 3,147 times.
    /// </summary>
    private ImmutableArray<string> PathQualifiedParameterNames(NavigationTarget target, PathQualifiedNode path)
    {
        // The `::foo` local form carries an empty path and means this file, which is the question
        // the unqualified route already answers.
        if ( path.Path.Length == 0 )
        {
            return UnqualifiedParameterNames(target, path.NameToken.Text);
        }

        FunctionSymbol? found = DatabaseQueries.FunctionInIncludeScope(
            target.Store,
            target.ContextId,
            askingPath: "",
            [RelativePathIndex.Normalize(path.Path)],
            NameTable.Shared.InternLower(path.NameToken.Text));

        return found is null ? default : [.. found.Parameters.Select(static p => p.Name)];
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
    private static string? EnclosingClassAt(NavigationTarget target, Position position)
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
