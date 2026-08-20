using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Parser;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;
using GSCode.Workspace.Api;
using GSCode.Workspace.Typing;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// One walk of the tree, shared by every rule whose judgement is about a single node.
///
/// Nine rules each descended the whole file on their own, and each descent visited the same
/// million nodes: a bo3 script's tree is around a thousand nodes and the corpus is a million, so
/// the traversal was being paid for nine times over to ask nine independent questions of each node.
/// Measured on bo3, a bare walk that visits and does nothing else is about 85 ms of a 2.1 s lint
/// pass, and `TypeMismatchLint` — one of the nine — cost 95 ms in total, so the walking was nearly
/// all of what those rules cost and their own predicates were almost free.
///
/// A rule qualifies for this pass when its own walk was PURE PASS-THROUGH: look at the node, then
/// recurse into every child unconditionally. Nine were. The ones left out are left out for a
/// reason, not for lack of attention:
///
/// <list type="bullet">
/// <item>`ThreadedResultLint` treats an expression differently depending on whether its value is
/// consumed, so it threads a flag down the descent.</item>
/// <item>`UnusedLocalLint`, `UnusedBindingLint` and `UnassignedVariableLint` each build per-function
/// state, so their walk is scoped to a declaration rather than to the file.</item>
/// <item>`ArgumentCountLint` and `DevBlockCallLint` carry a lookup cache and a namespace set down
/// their walks, and the second reads reference entries rather than the tree.</item>
/// </list>
///
/// Each rule keeps its own `Analyze`, which still walks on its own — that is what the rule's unit
/// tests exercise, and what an offline caller with one file and no lint pass uses. This type calls
/// the same per-node methods those walks call, so there is one copy of each judgement.
///
/// The diagnostics all land in one builder and `WorkspaceLints` sorts by position before returning,
/// so merging the walks does not change what is published: the order is a property of the file, not
/// of which rule ran when.
/// </summary>
internal static class NodeLintPass
{
    /// <summary>
    /// Everything the per-node rules need that does not change from node to node. Passed by
    /// <c>in</c> so the descent does not copy it at every step.
    /// </summary>
    private readonly struct Context
    {
        public Context(
            BuiltinApi builtins,
            ScriptTypes types,
            HashSet<string> globals,
            bool voidResults,
            bool expressionStatements)
        {
            Builtins = builtins;
            Types = types;
            Globals = globals;
            VoidResults = voidResults;
            ExpressionStatements = expressionStatements;
        }

        public BuiltinApi Builtins { get; }

        public ScriptTypes Types { get; }

        public HashSet<string> Globals { get; }

        /// <summary>False on a game with no bundled library, where the rule cannot speak.</summary>
        public bool VoidResults { get; }

        /// <summary>False on a file the parser could not read, where the rule's premise fails.</summary>
        public bool ExpressionStatements { get; }
    }

    /// <summary>
    /// Runs the nine per-node rules over the file in one descent, appending to
    /// <paramref name="diagnostics"/>.
    /// </summary>
    /// <param name="types">
    /// The flow typer's answer for this file, which `TypeMismatchLint` reads. It is memoised per
    /// parse, so asking for it here costs nothing beyond the walk the field-write rules already
    /// paid for.
    /// </param>
    internal static void Run(
        ParseResult result,
        BuiltinApi builtins,
        ScriptTypes types,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        Context context = new(
            builtins,
            types,
            GlobalObjectWriteLint.GlobalNames(),
            VoidResultLint.Applies(builtins),
            ExpressionStatementLint.Applies(result));

        Visit(result.Tree.Root, in context, diagnostics);
    }

    private static void Visit(AstNode node, in Context context, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ArithmeticLint.InspectNode(node, diagnostics);
        ConstDeclarationLint.InspectNode(node, diagnostics);
        GlobalObjectWriteLint.InspectNode(node, context.Globals, diagnostics);
        PreferBooleanLiteralLint.InspectNode(node, context.Builtins, diagnostics);
        TypeMismatchLint.InspectNode(node, context.Types, diagnostics);

        if ( context.VoidResults )
        {
            VoidResultLint.InspectNode(node, context.Builtins, diagnostics);
        }

        if ( context.ExpressionStatements )
        {
            ExpressionStatementLint.InspectNode(node, diagnostics);
        }

        // Two rules look only at statements, and their own walks stop at the first expression rather
        // than descending through every operand in the file. This pass has to descend anyway for the
        // rules above, so the restriction is applied by not ASKING them about an expression — which
        // is the same set of nodes their own walks would have reached.
        if ( node is not ExprNode )
        {
            CaseLabelLint.InspectNode(node, diagnostics);
            UnreachableCodeLint.InspectNode(node, diagnostics);
        }

        foreach ( AstNode child in AstSearch.ChildrenOf(node) )
        {
            Visit(child, in context, diagnostics);
        }
    }
}
