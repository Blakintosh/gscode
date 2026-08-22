using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Lexing;
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

        CollectFromDeclaration(result.Tree.Root, diagnostics);

        return diagnostics.ToImmutable();
    }

    /// <summary>
    /// Finds every body in the file, wherever it is nested — a class member, or a declaration
    /// inside a top-level <c>/# … #/</c>. Descends through <see cref="AstSearch.ChildrenOf"/>
    /// rather than naming each container, so a container added later is searched without this rule
    /// having to learn about it.
    ///
    /// A CONSTRUCTOR and a DESTRUCTOR are deliberately not inspected, and this is not an oversight.
    /// This rule scopes names per body, with no model of a class's <c>var</c> members, and inside a
    /// class method a bare name may be a member rather than a local. A constructor exists to
    /// initialise members it never itself reads, so every such write looks exactly like a dead
    /// store. Inspecting them added 103 findings over BO3's scripts, and the first one sampled —
    /// <c>id = undefined;</c> in <c>_driving_fx.csc</c>'s <c>GroundFx</c> constructor — is a member
    /// declared <c>var id;</c> and read by that class's <c>play()</c>. Reaching them needs member
    /// resolution first, not a wider walk.
    ///
    /// <see cref="UnusedBindingLint"/> does inspect all three, which is not a contradiction: it asks
    /// about PARAMETERS, and a parameter is scoped to its own body whatever the class holds.
    /// </summary>
    private static void CollectFromDeclaration(AstNode element, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        switch ( element )
        {
            case FunctionNode function:
                InspectBody(function.Parameters, function.Body, diagnostics);
                return;

            case ConstructorNode:
            case DestructorNode:
                return;

            default:
                foreach ( AstNode child in AstSearch.ChildrenOf(element) )
                {
                    CollectFromDeclaration(child, diagnostics);
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

        Collect(body, firstWrite, read);

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

    /// <summary>
    /// Walks a function body, recording the first WRITE of each name and every name READ.
    ///
    /// Descends through <see cref="AstSearch.ChildrenOf"/> rather than a switch over every node
    /// kind, the same way <see cref="UnassignedVariableLint"/> does. The interesting nodes are few
    /// — assignments, the two binding forms, and the one place an identifier is a function name
    /// rather than a value — and enumerating children generically means a node type added later is
    /// traversed without this rule having to learn about it.
    /// </summary>
    private static void Collect(AstNode node, Dictionary<string, PToken> firstWrite, HashSet<string> read)
    {
        switch ( node )
        {
            case AssignmentNode assignment:
                // `x = value` writes x. `x += value` READS x as well, so it can never be a dead
                // store on its own.
                if ( assignment.Target is IdentifierNode target )
                {
                    if ( assignment.Operator == TokenKind.Assign )
                    {
                        RecordWrite(target.Token, firstWrite);
                    }
                    else
                    {
                        read.Add(target.Token.Text);
                    }
                }
                else
                {
                    // self.foo = … — a field, whose reader may be another script entirely.
                    Collect(assignment.Target, firstWrite, read);
                }

                Collect(assignment.Value, firstWrite, read);
                return;

            case ForeachNode foreachNode:
                // A loop variable is bound by the loop, not assigned by the author, and an unused
                // `key` in `foreach ( key, value in … )` is idiomatic rather than dead.
                if ( foreachNode.KeyToken is not null )
                {
                    read.Add(foreachNode.KeyToken.Value.Text);
                }

                read.Add(foreachNode.ValueToken.Text);
                Collect(foreachNode.Collection, firstWrite, read);
                Collect(foreachNode.Body, firstWrite, read);
                return;

            case ConstDeclNode constDecl:
                RecordWrite(constDecl.NameToken, firstWrite);
                Collect(constDecl.Value, firstWrite, read);
                return;

            case IdentifierNode identifier:
                read.Add(identifier.Token.Text);
                return;

            case CallNode call:
                // Target is the object a method is called ON — `self` in `self foo()`.
                if ( call.Target is not null )
                {
                    Collect(call.Target, firstWrite, read);
                }

                // The callee of `foo()` names a FUNCTION, so it is not a read of a local called
                // foo. `[[ handler ]]()` is different: that really does read the local.
                if ( call.Callee is not (IdentifierNode or QualifiedNode or PathQualifiedNode) )
                {
                    Collect(call.Callee, firstWrite, read);
                }

                foreach ( ExprNode argument in call.Arguments )
                {
                    Collect(argument, firstWrite, read);
                }

                return;

            default:
                foreach ( AstNode child in AstSearch.ChildrenOf(node) )
                {
                    Collect(child, firstWrite, read);
                }

                return;
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
