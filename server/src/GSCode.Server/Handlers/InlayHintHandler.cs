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
using ParameterMemo = System.Collections.Generic.Dictionary<(string? Scope, string? Qualifier, string Name), System.Collections.Immutable.ImmutableArray<string>>;

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
    /// frame, and a fresh <see cref="FlowTyper"/> per request would re-walk the whole file each time.
    /// Keyed by <see cref="ParseResult"/> reference rather than path+version:
    /// <c>AnalyzeIfStale</c> already guarantees an unchanged document hands back the SAME instance,
    /// the fact <c>FlowTyper.InferValues</c>'s own memoisation relies on too. A ConditionalWeakTable needs
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
        // Resolution is answered once per distinct callee, not once per call site. A script calls
        // the same handful of names over and over, and each miss is a store query — per declared
        // namespace on BO3, over the include scope on the merge dialects — so a file calling
        // is_player() fifty times asked the same question fifty times, on every request, while
        // scrolling sends one request per frame.
        //
        // Per REQUEST and thrown away with it, for FunctionLookupCache's reason: an answer that
        // outlived the request would have to be invalidated by an edit anywhere in the workspace,
        // which is a subscription problem rather than a dictionary.
        ParameterMemo memo = new();

        // What the reader sees where a macro was invoked, keyed by where the expansion's tokens
        // report themselves. An expanded argument's node is the VALUE — `DELETE_TRIGGER` is the
        // literal 1 by the time there is a tree — so the name on screen is recoverable only from
        // the invocation list, and SaysItsOwnName has nothing to compare against without it.
        Dictionary<Position, string> macroNames = MacroNamesByPosition(target);

        // Per call site, because resolving one can run a store lookup per declared namespace. The
        // client sends one of these per visible range, so scrolling produces a request per frame
        // and cancels the ones it has scrolled past.
        foreach ( ExprNode node in CollectCalls(target.Result.Tree.Root, window) )
        {
            cancellationToken.ThrowIfCancellationRequested();

            ImmutableArray<ExprNode> arguments = node switch
            {
                CallNode call => call.Arguments,
                ArrowCallNode arrowCall => arrowCall.Arguments,
                _ => [],
            };

            // OVERLAPS, not "starts inside". A call's arguments can be on screen while its callee
            // is a line or two above — every multi-line argument list at the top of the viewport —
            // and testing the call's start would drop all of its labels until the name scrolled into
            // view. Which labels come out is then decided per argument, against each label's position.
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

            ImmutableArray<string> parameters = ResolveParameterNames(target, types, node, memo);
            if ( parameters.IsDefaultOrEmpty )
            {
                continue;
            }

            int count = Math.Min(parameters.Length, arguments.Length);
            for ( int index = 0; index < count; index++ )
            {
                Position position = arguments[index].Range.Start;
                if ( window.Contains(position) && !SaysItsOwnName(arguments[index], parameters[index], macroNames) )
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
    ///
    /// A MACRO NAME counts as one of those bare words, because a macro name is what the reader has
    /// in front of them. <c>craftable_trigger_think( ..., DELETE_TRIGGER, PERSISTENT )</c> against
    /// <c>delete_trigger</c> and <c>persistent</c> parameters is the same repetition spelled in
    /// capitals, and the corpus sweep found it in the shipped zombie scripts. The tree cannot see
    /// it — the argument node there is the literal the macro expands to — so the name comes from
    /// <paramref name="macroNames"/>, which the preprocessor recorded at the invocation site.
    /// </summary>
    private static bool SaysItsOwnName(
        ExprNode argument, string parameterName, Dictionary<Position, string> macroNames)
    {
        if ( argument is IdentifierNode identifier )
        {
            return string.Equals(identifier.Token.Text, parameterName, StringComparison.OrdinalIgnoreCase);
        }

        return macroNames.TryGetValue(argument.Range.Start, out string? invoked)
            && string.Equals(invoked, parameterName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every macro invocation written in THIS file, by the position its expansion's tokens report.
    ///
    /// One reached through an <c>#insert</c> carries the header's coordinates, which would collide
    /// with unrelated positions in the root file, so those are left out exactly as the macro hint
    /// family leaves them out.
    ///
    /// Built once per request rather than searched per argument: the list is short, the arguments
    /// are not, and the client sends one request per visible range while scrolling.
    /// </summary>
    private static Dictionary<Position, string> MacroNamesByPosition(NavigationTarget target)
    {
        Dictionary<Position, string> names = [];

        foreach ( MacroInvocation invocation in target.Result.Preprocessed.MacroInvocations )
        {
            if ( invocation.SourceFile is null )
            {
                names[invocation.Range.Start] = invocation.Name;
            }
        }

        return names;
    }

    /// <summary>
    /// Parameter names for one call site, whichever of the four callee forms it uses.
    ///
    /// The two indirect forms — <c>[[ ptr ]]( ... )</c> and <c>[[ obj ]]-&gt;method( ... )</c> — are
    /// answered from the flow pass rather than from the syntax, because the callee is a VALUE there
    /// and the syntax names a local. A pointer call is how most of a Black Ops III script's dispatch
    /// is written — the majority of calls in some files.
    /// </summary>
    private ImmutableArray<string> ResolveParameterNames(
        NavigationTarget target, ScriptTypes types, ExprNode node, ParameterMemo memo)
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

            // The pointer's TARGET is a name like any other, so it shares the memo with the
            // written forms below: the same function reached through a pointer and by name is one
            // question asked twice.
            (string? Scope, string? Qualifier, string Name) pointerKey = (null, reference.Namespace, reference.Name);
            if ( memo.TryGetValue(pointerKey, out ImmutableArray<string> cachedPointer) )
            {
                return cachedPointer;
            }

            ImmutableArray<string> resolved = reference.Namespace is null
                ? UnqualifiedParameterNames(target, reference.Name)
                : QualifiedParameterNames(target, reference.Namespace, reference.Name);

            memo[pointerKey] = resolved;
            return resolved;
        }

        return ResolveNamedParameterNames(target, call, memo);
    }

    /// <summary>
    /// The three WRITTEN callee forms — a bare name, <c>ns::name</c>, and the path form — answered
    /// once per distinct callee. The enclosing class is part of the key rather than of the answer:
    /// a bare name inside a class body means a method first, and two classes in one file can spell
    /// the same call differently.
    /// </summary>
    private ImmutableArray<string> ResolveNamedParameterNames(NavigationTarget target, CallNode call, ParameterMemo memo)
    {
        if ( call.Callee is IdentifierNode identifier )
        {
            string? scope = CallResolution.EnclosingClassAt(target.Result, call.Range.Start);
            (string? Scope, string? Qualifier, string Name) key =
                (scope, null, NameTable.Shared.InternLower(identifier.Token.Text));

            if ( memo.TryGetValue(key, out ImmutableArray<string> cached) )
            {
                return cached;
            }

            ImmutableArray<string> answer = BareParameterNames(target, identifier, scope);
            memo[key] = answer;
            return answer;
        }

        if ( call.Callee is QualifiedNode qualified )
        {
            (string? Scope, string? Qualifier, string Name) key = (
                null,
                NameTable.Shared.InternLower(qualified.NamespaceToken.Text),
                NameTable.Shared.InternLower(qualified.NameToken.Text));

            if ( memo.TryGetValue(key, out ImmutableArray<string> cached) )
            {
                return cached;
            }

            ImmutableArray<string> answer = QualifiedParameterNames(
                target, qualified.NamespaceToken.Text, qualified.NameToken.Text);

            memo[key] = answer;
            return answer;
        }

        if ( call.Callee is PathQualifiedNode path )
        {
            (string? Scope, string? Qualifier, string Name) key = (
                null, path.Path, NameTable.Shared.InternLower(path.NameToken.Text));

            if ( memo.TryGetValue(key, out ImmutableArray<string> cached) )
            {
                return cached;
            }

            ImmutableArray<string> answer = PathQualifiedParameterNames(target, path);
            memo[key] = answer;
            return answer;
        }

        return default;
    }

    /// <summary>A bare callee: the enclosing class's method where there is one, else the file's scope.</summary>
    private ImmutableArray<string> BareParameterNames(
        NavigationTarget target, IdentifierNode identifier, string? enclosingClass)
    {
        // Inside a class body a bare name is a method first — so this has to be asked before the
        // namespace and builtin lookups below, or an inherited method's hints come out as some
        // unrelated engine function's parameter names.
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

    /// <summary>A bare name: a script function the file's scope reaches, else a builtin.</summary>
    private ImmutableArray<string> UnqualifiedParameterNames(NavigationTarget target, string name)
    {
        // Interned once outside the resolver, which compares ordinally.
        string keyName = NameTable.Shared.InternLower(name);

        FunctionSymbol? script = CallResolution.UnqualifiedFunction(
            target.Store, target.ContextId, target.Result, keyName);

        if ( script is not null )
        {
            return [.. script.Parameters.Select(static p => p.Name)];
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

        FunctionSymbol? found = CallResolution.PathQualifiedFunction(
            target.Store, target.ContextId, path.Path, NameTable.Shared.InternLower(path.NameToken.Text));

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

    /// <summary>
    /// Every call site the window can see — the four <c>CallNode</c> forms and arrow method calls.
    ///
    /// Pruned as it descends rather than filtered afterwards. A request covers a screenful, the
    /// client sends one per visible range, and scrolling fires one per frame, so walking all of
    /// <c>_zm.gsc</c> to keep twenty nodes would be the whole of the walk's cost repeated per frame.
    /// A parser range spans everything the node contains, so a subtree that misses the window
    /// entirely holds no call that could hit it.
    ///
    /// A node whose range is EMPTY is descended into regardless. Error recovery is the normal
    /// state here, and a node that never got a real range would otherwise take its children with
    /// it — a silent loss of hints on exactly the half-written code this handler runs against
    /// most.
    /// </summary>
    private static IEnumerable<ExprNode> CollectCalls(AstNode root, TextRange window)
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
                if ( child.Range.End > child.Range.Start && !window.Overlaps(child.Range) )
                {
                    continue;
                }

                stack.Push(child);
            }
        }
    }
}
