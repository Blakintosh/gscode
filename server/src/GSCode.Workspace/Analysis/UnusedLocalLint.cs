using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// Reports a local that is assigned and never read — <c>function f() { bar = undefined; }</c>.
///
/// Hint severity with the Unnecessary tag, deliberately. Dead code is worth knowing about but is
/// not a defect: the script runs, and half-finished work in progress is the normal reason to have
/// one. Anything louder would be nagging someone mid-edit.
///
/// It was Information, which put every one in the editor's problem list — 1,716 of them over MW2's
/// scripts alone, and 4,711 across the five games, all in code that ships and works. A list that
/// long is one nobody reads. The tag is what carries the finding: the editor greys the name either
/// way, so the signal survives and only the list entry goes. Every other rule of this kind here
/// (5020, 5012, 5001, 5002) was already a Hint.
///
/// 5015 is the exception that shows what the number decides rather than the category: unreachable
/// code is the same kind of finding, and it is Information, because it fires 48 times across all
/// five corpora rather than 4,711.
///
/// Reads and writes are told apart structurally rather than by counting occurrences. A name is
/// READ wherever it appears except as the direct target of a plain <c>=</c>; a compound assignment
/// (<c>+=</c>) reads its target, and so does <c>x++</c>, which is why those do not count as
/// dead stores.
///
/// Only plain locals are considered. <c>self.foo</c> and <c>level.bar</c> are fields with lives of
/// their own — another script may read them — so an unread write to one says nothing.
/// </summary>
public static class UnusedLocalLint
{
    public static ImmutableArray<Diagnostic> Analyze(ParseResult result)
    {
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        CollectFromDeclaration(result.Tree.Root, diagnostics, insideClass: false);

        return diagnostics.ToImmutable();
    }

    /// <summary>
    /// Finds every body in the file, wherever it is nested — a class member, or a declaration
    /// inside a top-level <c>/# … #/</c>. Descends through <see cref="AstSearch.ChildrenOf"/>
    /// rather than naming each container, so a container added later is searched without this rule
    /// having to learn about it.
    ///
    /// Nothing inside a CLASS is inspected — every method, not only a constructor or destructor —
    /// and this is not an oversight. This rule scopes names per body, with no model of a class's
    /// <c>var</c> members, and inside a class method a bare name may be a member rather than a
    /// local; a member write that no method IN THIS BODY reads back looks exactly like a dead
    /// store. Real BO3 code hits this on an ordinary setter —
    /// <c>function set_door_paths( p ) { m_n_door_connect_paths = p; }</c> in
    /// <c>scripts\shared\doors_shared.gsc</c> — where the member is read by another method
    /// entirely. Constructors were the first case found (<c>id = undefined;</c> in
    /// <c>_driving_fx.csc</c>'s <c>GroundFx</c> constructor, read by that class's <c>play()</c>),
    /// but the same reasoning always applied to every method — reaching a member needs member
    /// resolution first, not a wider walk.
    ///
    /// <see cref="UnassignedVariableLint"/> already takes this same all-or-nothing view of a class,
    /// via its own <c>insideClass</c>. <see cref="UnusedBindingLint"/> does inspect every method,
    /// which is not a contradiction: it asks about PARAMETERS, and a parameter is scoped to its own
    /// body whatever the class holds.
    /// </summary>
    private static void CollectFromDeclaration(AstNode element, ImmutableArray<Diagnostic>.Builder diagnostics, bool insideClass)
    {
        switch ( element )
        {
            case FunctionNode function when !insideClass:
                InspectBody(function.Parameters, function.Body, diagnostics);
                return;

            case FunctionNode:
            case ConstructorNode:
            case DestructorNode:
                return;

            case ClassNode classNode:
                foreach ( AstNode member in classNode.Members )
                {
                    CollectFromDeclaration(member, diagnostics, insideClass: true);
                }

                return;

            default:
                foreach ( AstNode child in AstSearch.ChildrenOf(element) )
                {
                    CollectFromDeclaration(child, diagnostics, insideClass);
                }

                return;
        }
    }

    private static void InspectBody(
        ImmutableArray<ParameterNode> parameters,
        BlockNode body,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        // First write per name, in source order, and every name ever read.
        Dictionary<string, PToken> firstWrite = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> read = new(StringComparer.OrdinalIgnoreCase);

        // A parameter is not a dead store: the caller supplied it, and an unread one is a
        // different finding with a different rule.
        foreach ( ParameterNode parameter in parameters )
        {
            read.Add(parameter.NameToken.Text);
        }

        // Only a plain `x = v` (or a const) is a store that can be dead. A compound assignment
        // reads the old value; `a[ i ] = v` and `a.f = v` use what `a` already held; a foreach
        // variable and a waittill output are bound by the loop and the engine, and an unused one
        // there is idiomatic rather than dead. So all of those count as uses of the name.
        foreach ( LocalUse use in LocalUses.Of(body) )
        {
            if ( use.Kind == LocalUseKind.Assign )
            {
                RecordWrite(use.Token, firstWrite);
            }
            else
            {
                read.Add(use.Token.Text);
            }
        }

        foreach ( KeyValuePair<string, PToken> write in firstWrite )
        {
            if ( read.Contains(write.Key) )
            {
                continue;
            }

            // Macro-supplied names are not the author's to remove, and the range would point at
            // the invocation rather than at anything they wrote.
            if ( write.Value.Provenance.DefinitionSite is not null )
            {
                continue;
            }

            Diagnostic unused = Diagnostic.Create(
                write.Value.RootRange,
                DiagnosticSeverity.Hint,
                GscDiagnosticCode.UnusedLocal,
                write.Value.Text);

            diagnostics.Add(unused with { Tags = [DiagnosticTag.Unnecessary] });
        }
    }

    private static void RecordWrite(PToken nameToken, Dictionary<string, PToken> firstWrite)
    {
        // The FIRST write is the one reported: it is where the name is introduced, and a later
        // one is only dead because the first was too.
        if ( !firstWrite.ContainsKey(nameToken.Text) )
        {
            firstWrite[nameToken.Text] = nameToken;
        }
    }
}
