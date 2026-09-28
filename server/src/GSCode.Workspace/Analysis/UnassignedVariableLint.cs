using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Parser;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// Reports a local READ that nothing in its function ever writes — <c>switch ( never_set )</c>,
/// <c>foo = not_defined + 5</c>. In GSC an undefined variable is not a compile error, so this
/// surfaces at runtime as an undefined value propagating somewhere far away from the typo.
///
/// The whole difficulty is false positives, since a name can arrive without an assignment the
/// walk can see. Everything below is excluded, and each exclusion is a way that legitimately
/// happens:
///
/// * <b>Parameters</b> — supplied by the caller.
/// * <b>The parameter pack</b> — <c>...</c> in the declaration binds a name that appears nowhere
///   in the source (BO3's <c>vararg</c>). Reading it in a function that does NOT declare <c>...</c>
///   is a real mistake, and one this rule is already positioned to catch, so it reports 5024 there
///   rather than the generic message.
/// * <b>Loop bindings</b> — <c>foreach ( key, value in … )</c> binds both.
/// * <b>Globals</b> — <c>level</c>, <c>self</c> and friends come from the profile, and are only
///   identifiers on dialects where they are not keywords.
/// * <b>Macro-supplied names</b> — the range would point into an expansion rather than at
///   anything the author wrote, and the name is not theirs to fix.
/// * <b>Anything written ANYWHERE in the function</b>, including below the read. Order is
///   deliberately ignored: a loop legitimately reads on the second pass what it wrote on the
///   first, and use-before-assignment is a different and much harder question than never-assigned.
/// * <b>Class methods</b> — a member can be reachable unqualified, so the function's own writes
///   are not the whole story there.
///
/// Reported as a Warning rather than an Error. It is a strong signal but not a certainty, and the
/// rule that an Error must never land on working code matters more than the severity does.
/// </summary>
public static class UnassignedVariableLint
{
    public static ImmutableArray<Diagnostic> Analyze(ParseResult result, GameProfile? profile = null)
    {
        // An unresolved import makes this unanswerable. A `#define`d constant whose definition
        // never arrived survives preprocessing as a plain identifier, which is indistinguishable
        // from a variable nobody assigned — MW2's scripts alone produced 755 of them, every one an
        // ALL_CAPS constant from a header the corpus could not resolve.
        //
        // The same gate FunctionResolutionLint uses, for the same reason: when the set of names
        // legally in scope is unknowable, "nothing assigns this" is not a claim worth making.
        if ( ImportGate.AnyMacrosLost(result, GscDiagnosticCode.UsingNotFound) )
        {
            return [];
        }

        GameProfile game = profile ?? GameProfile.Active;
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        // The Infinity Ward dialects allow a constant at FILE scope — `BRIDGE_COLLAPSE_SPEED = 1.0;`
        // sitting between two functions, readable from all of them. The parser has always modelled
        // these (FileScopeConstantNode, gated on HasFileScopeConstants); this rule looked only
        // inside functions, so every read of one was reported. MW2's scripts alone produced 755,
        // and their ALL_CAPS naming made them look convincingly like macros from a header.
        HashSet<string> fileScope = new(StringComparer.OrdinalIgnoreCase);
        CollectFileScopeConstants(result.Tree.Root.Elements, fileScope);

        foreach ( AstNode element in result.Tree.Root.Elements )
        {
            InspectDeclaration(element, game, fileScope, diagnostics, insideClass: false);
        }

        return diagnostics.ToImmutable();
    }

    /// <summary>Names declared at file scope, including inside a dev block at that level.</summary>
    private static void CollectFileScopeConstants(IEnumerable<AstNode> elements, HashSet<string> fileScope)
    {
        foreach ( AstNode element in elements )
        {
            switch ( element )
            {
                case FileScopeConstantNode constant:
                    fileScope.Add(constant.NameToken.Text);
                    continue;
                case DevBlockDeclNode devBlock:
                    CollectFileScopeConstants(devBlock.Declarations, fileScope);
                    continue;
            }
        }
    }

    private static void InspectDeclaration(
        AstNode element, GameProfile game, HashSet<string> fileScope,
        ImmutableArray<Diagnostic>.Builder diagnostics, bool insideClass)
    {
        switch ( element )
        {
            case FunctionNode function when !insideClass:
                InspectFunction(function, game, fileScope, diagnostics);
                return;

            case ClassNode classNode:
                // A class method may reach a member without qualifying it, so the function's own
                // writes do not account for every name it can legally read.
                foreach ( AstNode member in classNode.Members )
                {
                    InspectDeclaration(member, game, fileScope, diagnostics, insideClass: true);
                }

                return;

            case DevBlockDeclNode devBlock:
                foreach ( AstNode declaration in devBlock.Declarations )
                {
                    InspectDeclaration(declaration, game, fileScope, diagnostics, insideClass);
                }

                return;

            default:
                return;
        }
    }

    private static void InspectFunction(
        FunctionNode function, GameProfile game, HashSet<string> fileScope,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        HashSet<string> assigned = new(fileScope, StringComparer.OrdinalIgnoreCase);
        List<PToken> reads = [];

        foreach ( ParameterNode parameter in function.Parameters )
        {
            assigned.Add(parameter.NameToken.Text);
        }

        foreach ( string global in game.GlobalObjectNames )
        {
            assigned.Add(global);
        }

        // Flow-insensitive on purpose: every kind of write binds the name, and only a name no
        // write anywhere in the body binds is reported.
        foreach ( LocalUse use in LocalUses.Of(function.Body) )
        {
            if ( use.IsWrite )
            {
                assigned.Add(use.Token.Text);
            }
            else
            {
                reads.Add(use.Token);
            }
        }

        foreach ( PToken read in reads )
        {
            if ( assigned.Contains(read.Text) )
            {
                continue;
            }

            // Not the author's name to fix, and the range points into an expansion.
            if ( read.Provenance.DefinitionSite is not null )
            {
                continue;
            }

            // The pack is bound by `...` in the declaration, so it is never in the assigned set and
            // reaches here like any unbound name. Where the function DOES declare `...` it is bound
            // and there is nothing to say; where it does not, "never assigned" is the wrong advice —
            // nobody assigns the pack, they add `...` — so 5024 says that instead.
            //
            // By token kind rather than by name: the dialect gate comes free (on a game without the
            // pack the word lexes as a plain identifier and gets the ordinary treatment), and a
            // local the author assigned themselves never gets here at all.
            if ( read.Kind == TokenKind.Vararg )
            {
                if ( !function.HasVarargs )
                {
                    diagnostics.Add(Diagnostic.Create(
                        read.RootRange,
                        DiagnosticSeverity.Warning,
                        GscDiagnosticCode.VarargOutsideVarargFunction,
                        read.Text));
                }

                continue;
            }

            // MW2's running thread. Nobody assigns it — the engine supplies it wherever a function
            // body reads it — so "never assigned in this function" is simply wrong about it. By kind
            // again, so the dialect gate comes free: on a game whose keyword set lacks the word it
            // lexes as a plain identifier and an unassigned `thisthread` is reported as usual.
            if ( read.Kind == TokenKind.ThisThread )
            {
                continue;
            }

            // CAUTION for whoever restores v1.5's `StoreFunctionAsPointer`, which reports a bare
            // identifier that names a FUNCTION being used as a value: it fires on exactly this
            // range, and this diagnostic reaches the name first. It has to REPLACE this one rather
            // than stack a second squiggle on the same identifier — two diagnostics for one
            // mistake, and the less useful of them arriving first. See FOLLOWUPS.md.
            diagnostics.Add(Diagnostic.Create(
                read.RootRange,
                DiagnosticSeverity.Warning,
                GscDiagnosticCode.VariableNeverAssigned,
                read.Text));
        }
    }
}
