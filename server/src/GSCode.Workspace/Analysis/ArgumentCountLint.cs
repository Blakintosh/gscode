using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// Reports a call passing the wrong number of arguments.
///
/// The rule is NOT the same on both sides, and getting that wrong is what makes a naive arity check
/// unusable on GSC:
///
/// * A <b>builtin</b> is validated by the engine, so both bounds are real: fewer than its mandatory
///   parameters or more than it declares at all. Reported against the UNION of its overloads — a
///   name with a 1-argument and a 3-argument form accepts either, and judging one overload would
///   flag the correct call.
/// * A <b>script function</b> is only wrong when there are TOO MANY. Calling with fewer arguments
///   than declared is legal and idiomatic: the missing ones are simply <c>undefined</c>, and stock
///   scripts do it constantly. Enforcing a lower bound there would flag thousands of correct calls.
///
/// Both are Errors, because both fail at link time rather than degrading — the script does not load.
/// That is only defensible because the check refuses to run wherever the answer is not certain:
/// varargs, an unresolved name, an ambiguous one, or a call whose text came from a macro.
/// </summary>
public static class ArgumentCountLint
{
    public static ImmutableArray<Diagnostic> Analyze(
        ParseResult result,
        LanguageStore store,
        string contextId,
        string path,
        BuiltinApi builtins,
        GameProfile? profile = null)
    {
        GameProfile game = profile ?? GameProfile.Active;
        ImmutableArray<string> askingNamespaces = DatabaseQueries.DeclaredNamespaces(result);
        HashSet<string> ownNamespace = OwnNamespaceFunctions(result, store, contextId, path, askingNamespaces);
        FunctionLookupCache lookups = new(store, contextId, path, askingNamespaces);

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        foreach ( AstNode element in result.Tree.Root.Elements )
        {
            Walk(element, result, store, contextId, path, builtins, game, askingNamespaces, ownNamespace, lookups, diagnostics, null);
        }

        return diagnostics.ToImmutable();
    }

    /// <summary>
    /// The function names an unqualified call in this file can reach without a qualifier, AS THEY ARE
    /// SPELLED: the file's own declarations, plus everything else declared into the namespaces it
    /// declares into. Used for one decision only — whether a script function shadows the builtin of
    /// the same name.
    ///
    /// ORDINAL, and the corpus is why. Two shipped BO3 files settle it in opposite directions:
    ///
    /// * <c>scripts\shared\exploder_shared.gsc</c> declares <c>function earthquake()</c> taking
    ///   nothing, and 9 lines later calls <c>Earthquake( magnitude, duration, origin, radius )</c> —
    ///   the ENGINE function, four arguments;
    /// * <c>scripts\zm\_zm.gsc</c> declares <c>function spawnSpectator()</c> and later calls
    ///   <c>spawnSpectator()</c> — its OWN function, though BO3 also has an engine
    ///   <c>SpawnSpectator( origin, angles )</c>.
    ///
    /// Both files ship and work, and no case-insensitive rule explains both: it would either break
    /// the first (4 arguments to a 0-parameter function) or the second (0 arguments where 2 are
    /// mandatory). The spelling is what tells the two apart, and the authors clearly wrote it that
    /// way on purpose. Scoped deliberately to THIS tie-break — general script-to-script resolution
    /// stays case-insensitive, as the rest of the codebase has it.
    ///
    /// Gathered once per file rather than asked per call, and that is the whole reason it is a set.
    /// The builtin check it guards runs on nearly every call in a script, so a store lookup there
    /// would put a scan of every record on the hot path once per engine call; this pays one pass per
    /// namespace instead.
    ///
    /// The file's own declarations come from the parse in hand, so a function being typed right now
    /// already shadows the builtin of the same name — the store holds the last INDEXED copy and lags
    /// the buffer.
    /// </summary>
    private static HashSet<string> OwnNamespaceFunctions(
        ParseResult result,
        LanguageStore store,
        string contextId,
        string path,
        ImmutableArray<string> askingNamespaces)
    {
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach ( FunctionSymbol function in result.Extraction.Functions )
        {
            names.Add(function.Name);
        }

        foreach ( string declared in askingNamespaces )
        {
            foreach ( FunctionSymbol function in DatabaseQueries.FunctionsInNamespace(
                store, contextId, path, declared, askingNamespaces) )
            {
                names.Add(function.Name);
            }
        }

        return names;
    }

    /// <summary>
    /// <paramref name="ownerClass"/> is the class whose body this subtree is inside, mirroring the
    /// state extraction keeps. It is what tells a bare <c>play( a, b )</c> inside a class from one at
    /// file scope, and so which declaration's arity the call has to satisfy.
    /// </summary>
    private static void Walk(
        AstNode node,
        ParseResult result,
        LanguageStore store,
        string contextId,
        string path,
        BuiltinApi builtins,
        GameProfile game,
        ImmutableArray<string> askingNamespaces,
        HashSet<string> ownNamespace,
        FunctionLookupCache lookups,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string? ownerClass)
    {
        if ( node is ClassNode classNode )
        {
            ownerClass = classNode.NameToken.Text.ToLowerInvariant();
        }

        if ( node is CallNode call )
        {
            Inspect(call, store, contextId, path, builtins, game, askingNamespaces, ownNamespace, lookups, diagnostics, ownerClass);
        }

        // An arrow call is a method call by construction, so its arity is judged against the class
        // that declares the name — [[self]]->m() against this class's chain, any other receiver
        // against whichever single class declares it.
        if ( node is ArrowCallNode arrow )
        {
            InspectArrow(arrow, store, contextId, diagnostics, ownerClass);
        }

        foreach ( AstNode child in AstSearch.ChildrenOf(node) )
        {
            Walk(child, result, store, contextId, path, builtins, game, askingNamespaces, ownNamespace, lookups, diagnostics, ownerClass);
        }
    }

    private static void Inspect(
        CallNode call,
        LanguageStore store,
        string contextId,
        string path,
        BuiltinApi builtins,
        GameProfile game,
        ImmutableArray<string> askingNamespaces,
        HashSet<string> ownNamespace,
        FunctionLookupCache lookups,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string? ownerClass)
    {
        if ( !TryGetCalleeName(call, out string name, out string? namespaceName, out Core.Text.TextRange nameRange) )
        {
            return;
        }

        // Text that arrived through a macro is not the author's, and the range would point at the
        // invocation rather than at the arguments anyone wrote.
        if ( CameFromMacro(call) )
        {
            return;
        }

        int supplied = call.Arguments.Length;

        // METHODS BEFORE BUILTINS, and only for the shapes that can mean one. Inside a class body a
        // bare name is a method first — all 525 such calls in the stock scripts are — so consulting
        // the engine library first would judge `stop( a, b )` against a builtin named `stop` that
        // the call never reaches.
        SymbolKey written = namespaceName is null
            ? new SymbolKey(null, name.ToLowerInvariant(), SymbolKind.Function, ownerClass)
            : new SymbolKey(namespaceName.ToLowerInvariant(), name.ToLowerInvariant(), SymbolKind.Function);

        if ( ownerClass is not null || namespaceName is not null )
        {
            SymbolKey canonical = MethodResolution.Canonicalize(
                store, contextId, written, ReferenceKind.Call);

            if ( canonical.OwnerClass is not null )
            {
                InspectAgainstMethod(store, contextId, canonical, name, supplied, nameRange, diagnostics);
                return;
            }
        }

        // A builtin only when the CURRENT NAMESPACE does not declare the name, which is the engine's
        // own order: builtins are the fallback after the current namespace, and `sys::` exists as an
        // explicit alias precisely because a script function otherwise wins (see BuiltinFunction).
        //
        // This read the other way round, and judged an unqualified call against the engine library
        // whatever the script said. `scripts\zm\_zm.gsc` declares `function spawnSpectator()` taking
        // nothing and calls `self thread spawnSpectator()` 2,138 lines later; BO3's library has a
        // SpawnSpectator( origin, angles ) with both parameters mandatory, so the call the file makes
        // to its OWN function was reported as missing two arguments.
        if ( namespaceName is null && !ownNamespace.Contains(name) && builtins.Find(name) is BuiltinFunction builtin )
        {
            // Only where the SIGNATURES can be trusted, which is a different claim from the name
            // list being complete — see HasReliableBuiltinSignatures. On the reconstructed libraries
            // this reported 141, 280 and 157 findings on shipped CoD4, WaW and BO1 scripts.
            if ( game.HasReliableBuiltinSignatures )
            {
                InspectBuiltin(builtin, name, supplied, nameRange, diagnostics);
            }

            // A builtin either way, so no script-function lookup follows.
            return;
        }

        // LOWERCASED, because the store keys functions by FunctionSymbol.KeyName and matches it
        // ordinally. Passing the source spelling meant this lookup found nothing for any call not
        // written in lower case, so the script half of the rule — 5022 — silently never fired on a
        // camelCase name, which in GSC is most of them.
        ImmutableArray<ResolvedFunction> candidates = lookups.Lookup(
            game.KeyNamespace(namespaceName ?? ""), name.ToLowerInvariant(), includePrivate: true);

        // Nothing found, or several possibilities: 5013/5014 report the first and 5007 the second,
        // and picking one of several signatures to judge against would be a guess.
        if ( candidates.Length != 1 )
        {
            return;
        }

        FunctionSymbol declared = candidates[0].Function;

        // Varargs takes anything.
        if ( declared.HasVarargs )
        {
            return;
        }

        // ONLY the upper bound. Fewer arguments than declared is legal and idiomatic — the missing
        // ones are undefined — so a lower bound here would flag thousands of correct stock calls.
        if ( supplied > declared.Parameters.Length )
        {
            diagnostics.Add(Diagnostic.Create(
                nameRange,
                DiagnosticSeverity.Error,
                GscDiagnosticCode.TooManyArguments,
                name,
                declared.Parameters.Length,
                supplied));
        }
    }

    /// <summary>
    /// The arity of <c>[[receiver]]-&gt;name( ... )</c>. Judged only when exactly one declaration is
    /// in view: <c>[[self]]-&gt;</c> resolves through the enclosing class's chain, and any other
    /// receiver only when a single class in the workspace declares the name. Several declarers means
    /// several signatures, and choosing one to judge against would be a guess.
    /// </summary>
    private static void InspectArrow(
        ArrowCallNode arrow,
        LanguageStore store,
        string contextId,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string? ownerClass)
    {
        // Text that arrived through a macro is not the author's, and the range would point at the
        // invocation rather than at the arguments anyone wrote.
        if ( arrow.MethodToken.Provenance.DefinitionSite is not null )
        {
            return;
        }

        string name = arrow.MethodToken.Text;
        bool isSelf = arrow.Object.Pointer is IdentifierNode identifier
            && string.Equals(identifier.Token.Text, "self", StringComparison.OrdinalIgnoreCase);

        SymbolKey written = new(
            null, name.ToLowerInvariant(), SymbolKind.Function, isSelf ? ownerClass : null);

        SymbolKey canonical = MethodResolution.Canonicalize(
            store, contextId, written, ReferenceKind.MethodCall);

        if ( canonical.OwnerClass is null )
        {
            return;
        }

        InspectAgainstMethod(
            store, contextId, canonical, name, arrow.Arguments.Length, arrow.MethodToken.RootRange, diagnostics);
    }

    /// <summary>
    /// Compares a call against a resolved method's declared parameters, on the same terms as a
    /// function: only the upper bound, and never against varargs.
    /// </summary>
    private static void InspectAgainstMethod(
        LanguageStore store,
        string contextId,
        SymbolKey canonical,
        string name,
        int supplied,
        Core.Text.TextRange nameRange,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ImmutableArray<ResolvedFunction> methods = MethodResolution.LookupMethods(
            store, contextId, canonical.OwnerClass!, canonical.Name);

        // Same rule as for functions: several declarations means several signatures, and picking one
        // to judge against would be a guess. An overlay shadowing a raw class is the usual cause.
        if ( methods.Length != 1 )
        {
            return;
        }

        FunctionSymbol declared = methods[0].Function;
        if ( declared.HasVarargs )
        {
            return;
        }

        if ( supplied > declared.Parameters.Length )
        {
            diagnostics.Add(Diagnostic.Create(
                nameRange,
                DiagnosticSeverity.Error,
                GscDiagnosticCode.TooManyArguments,
                name,
                declared.Parameters.Length,
                supplied));
        }
    }

    private static void InspectBuiltin(
        BuiltinFunction builtin,
        string name,
        int supplied,
        Core.Text.TextRange nameRange,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if ( builtin.Overloads.Length == 0 )
        {
            return;
        }

        // The LOWER bound only, and that is a concession to the DATA rather than to the language.
        //
        // The engine really does validate both ends, so an upper bound was the intent here — but the
        // library under-declares. Checking it reported 634 errors across 134 shipped BO3 scripts,
        // and they were the library's fault, not the code's: `array( a, b, c )` is variadic and the
        // data lists one parameter, `Record3DText` takes six and the data lists one. A rule that
        // needs data this incomplete to be right is a rule that reports the gaps as user mistakes.
        //
        // The lower bound survives because it rests on a different fact: a parameter marked
        // mandatory in the data is one somebody documented as required, and under-declaring the
        // parameter LIST does not invent mandatory ones. Zero findings across all five corpora.
        //
        // Restoring the upper bound is a data problem, not a code one — see the harvest reports,
        // which are the same curation input the builtin libraries are built from.
        int lowest = int.MaxValue;

        foreach ( BuiltinOverload overload in builtin.Overloads )
        {
            int mandatory = 0;
            foreach ( BuiltinParameter parameter in overload.Parameters )
            {
                if ( parameter.Mandatory )
                {
                    mandatory++;
                }
            }

            // The union: an overload needing fewer is an overload this call may have meant.
            lowest = Math.Min(lowest, mandatory);
        }

        if ( lowest == int.MaxValue || supplied >= lowest )
        {
            return;
        }

        diagnostics.Add(Diagnostic.Create(
            nameRange,
            DiagnosticSeverity.Error,
            GscDiagnosticCode.WrongBuiltinArgumentCount,
            name,
            lowest,
            supplied));
    }

    /// <summary>
    /// The called name and where it is written, or false when the callee is not a name this rule can
    /// resolve — a function pointer (<c>[[ ptr ]]()</c>) or a path-qualified call into a file the
    /// index may not hold.
    /// </summary>
    private static bool TryGetCalleeName(
        CallNode call, out string name, out string? namespaceName, out Core.Text.TextRange nameRange)
    {
        switch ( call.Callee )
        {
            case IdentifierNode identifier:
                name = identifier.Token.Text;
                namespaceName = null;
                nameRange = identifier.Token.RootRange;
                return true;

            case QualifiedNode qualified:
                name = qualified.NameToken.Text;
                namespaceName = qualified.NamespaceToken.Text;
                nameRange = qualified.NameToken.RootRange;
                return true;

            default:
                name = "";
                namespaceName = null;
                nameRange = Core.Text.TextRange.Empty;
                return false;
        }
    }

    /// <summary>
    /// Whether any part of this call's text arrived through a macro expansion. Checked on the
    /// ARGUMENTS as well as the callee, because a macro supplying arguments changes the count while
    /// the author wrote none of them.
    /// </summary>
    private static bool CameFromMacro(CallNode call)
    {
        if ( call.Callee is IdentifierNode identifier
            && identifier.Token.Provenance.DefinitionSite is not null )
        {
            return true;
        }

        foreach ( AstNode child in AstSearch.ChildrenOf(call) )
        {
            if ( child is IdentifierNode argument
                && argument.Token.Provenance.DefinitionSite is not null )
            {
                return true;
            }
        }

        return false;
    }
}
