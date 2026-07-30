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

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        foreach ( AstNode element in result.Tree.Root.Elements )
        {
            Walk(element, result, store, contextId, path, builtins, game, askingNamespaces, diagnostics);
        }

        return diagnostics.ToImmutable();
    }

    private static void Walk(
        AstNode node,
        ParseResult result,
        LanguageStore store,
        string contextId,
        string path,
        BuiltinApi builtins,
        GameProfile game,
        ImmutableArray<string> askingNamespaces,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if ( node is CallNode call )
        {
            Inspect(call, store, contextId, path, builtins, game, askingNamespaces, diagnostics);
        }

        foreach ( AstNode child in AstSearch.ChildrenOf(node) )
        {
            Walk(child, result, store, contextId, path, builtins, game, askingNamespaces, diagnostics);
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
        ImmutableArray<Diagnostic>.Builder diagnostics)
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

        // A builtin first: the engine owns these names, and a script function shadowing one is a
        // different problem that 5013/5014 speak for.
        if ( namespaceName is null && builtins.Find(name) is BuiltinFunction builtin )
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

        ImmutableArray<ResolvedFunction> candidates = DatabaseQueries.LookupFunctions(
            store,
            contextId,
            path,
            game.KeyNamespace(namespaceName ?? ""),
            name,
            includePrivate: true,
            askingNamespaces: askingNamespaces);

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
