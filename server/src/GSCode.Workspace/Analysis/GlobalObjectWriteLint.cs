using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Parser;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// An assignment to one of the engine's global objects: <c>level = ...</c>, <c>anim = ...</c>.
/// The engine owns those names; a script writes to FIELDS on them and never to the name itself.
///
/// The names come from <see cref="GameProfile.GlobalObjectNames"/> rather than a table here, so
/// the rule follows the dialect. <c>world</c> exists in Black Ops III and not in Call of Duty 4,
/// and in Call of Duty 4 a local called <c>world</c> is an ordinary name that must not be reported.
///
/// Only a BARE name is a write to the global. <c>level.things = []</c> and <c>game[ "x" ] = 1</c>
/// write THROUGH it, which is the normal way to use one — the corpus is built out of them — so a
/// rule that looked at the base of a member or index expression would report every script there is.
///
/// <c>classes</c> is left out of the set the rule checks even where the profile lists it. It is
/// reachable as a name but is not something scripts write to or read as an object, and unlike the
/// other five there is no evidence about what the compiler does with an assignment to it. The
/// other five are certain, which is what an Error has to be.
/// </summary>
public static class GlobalObjectWriteLint
{
    /// <summary>
    /// The dialect's global object names, minus `classes`.
    ///
    /// Built once per file and handed to the per-node check, since the set is a property of the
    /// active profile rather than of the node being looked at.
    /// </summary>
    internal static HashSet<string> GlobalNames()
    {
        HashSet<string> globals = new(StringComparer.OrdinalIgnoreCase);
        foreach ( string name in GameProfile.Active.GlobalObjectNames )
        {
            if ( !string.Equals(name, "classes", StringComparison.OrdinalIgnoreCase) )
            {
                globals.Add(name);
            }
        }

        return globals;
    }

    public static ImmutableArray<Diagnostic> Analyze(ParseResult result)
    {
        HashSet<string> globals = GlobalNames();

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        Inspect(result.Tree.Root, globals, diagnostics);

        return diagnostics.ToImmutable();
    }

    private static void Inspect(
        AstNode node, HashSet<string> globals, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        InspectNode(node, globals, diagnostics);

        foreach ( AstNode child in AstSearch.ChildrenOf(node) )
        {
            Inspect(child, globals, diagnostics);
        }
    }

    /// <summary>
    /// This rule's whole judgement about ONE node, with no descent of its own, so
    /// <see cref="NodeLintPass"/> can run it from the shared walk. The name set comes from
    /// <see cref="GlobalNames"/>, built once per file rather than per node.
    /// </summary>
    internal static void InspectNode(
        AstNode node, HashSet<string> globals, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        switch ( node )
        {
            // Every assignment operator, not just '='. `level += 1` is as impossible as `level = 1`.
            case AssignmentNode { Target: IdentifierNode target }:
                Report(target, globals, diagnostics);
                break;

            case PostfixNode { Operand: IdentifierNode operand }:
                Report(operand, globals, diagnostics);
                break;
        }
    }

    private static void Report(
        IdentifierNode target, HashSet<string> globals, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        // A name that arrived from a macro expansion reports the INVOCATION's range, so the squiggle
        // would land on a call site the author did not write the assignment at.
        if ( target.Token.Provenance.DefinitionSite is not null || !globals.Contains(target.Token.Text) )
        {
            return;
        }

        diagnostics.Add(Diagnostic.Create(
            target.Range,
            DiagnosticSeverity.Error,
            GscDiagnosticCode.CannotAssignToGlobalObject,
            target.Token.Text));
    }
}
