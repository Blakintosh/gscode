using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Parser.Lexing;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;
using GSCode.Workspace.Typing;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// Two type-derived findings the union lattice made answerable: a non-array enumerated, and a vector
/// component that cannot be a number.
///
/// Both were ruled out while the lattice was flat, for the same recorded reason — <c>ScrType.Join</c>
/// collapsed any disagreement to Unknown, so a rule was silent where it was safe and wrong where it
/// was not. Real unions change the question into one <see cref="ScrValue.MustBe"/> can answer: every
/// possible type has to fail before anything is said.
///
/// Reads the per-node map rather than re-deriving anything, so a rule and the type the editor shows
/// can never disagree about what an expression means.
///
/// **A third rule was written, measured and withdrawn from this file**, and the reason is worth
/// keeping beside the two that survived. <c>OperatorNotSupportedOnTypes</c> asked the operator table
/// for its operand verdict — which <see cref="ScrOperators"/> already computes and the typer
/// discards — and reported 752 findings across the five corpora on code that ships and works. Two
/// causes, both instructive: the guard tested <c>IsUnknown</c>, which is exact equality with the
/// universe, so a value narrowed by <c>isdefined</c> (universe MINUS undefined) sailed past it; and
/// <c>vector + scalar</c>, which the table calls unsupported, appears throughout the stock scripts,
/// so the table is stricter than the engine. Neither is fixed by tightening the rule — the second
/// says the operator model itself is not yet good enough to diagnose from.
///
/// Nothing here fires on a value the flow could not type. That is not caution for its own sake: GSC
/// has no compiler to contradict a false report, and an unknown operand is the normal case rather
/// than the exception — parameters, script-function returns and array elements are all unknown by
/// construction.
/// </summary>
public static class TypeMismatchLint
{
    public static ImmutableArray<Diagnostic> Analyze(ParseResult result, FlowTyper typer)
    {
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        ScriptTypes types = typer.InferValues(result);
        Walk(result.Tree.Root, types, diagnostics);

        return diagnostics.ToImmutable();
    }

    private static void Walk(AstNode node, ScriptTypes types, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        switch ( node )
        {
            case ForeachNode foreachNode:
                InspectEnumeration(foreachNode, types, diagnostics);
                break;

            case VectorNode vector:
                InspectVectorComponents(vector, types, diagnostics);
                break;
        }

        foreach ( AstNode child in AstSearch.ChildrenOf(node) )
        {
            Walk(child, types, diagnostics);
        }
    }

    /// <summary>
    /// <c>foreach</c> over something that cannot be enumerated. Only a value that is CERTAINLY a
    /// scalar counts — a struct is excluded deliberately, since the engine enumerates one.
    /// </summary>
    private static void InspectEnumeration(
        ForeachNode foreachNode, ScriptTypes types, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if ( !types.TryGetValue(foreachNode.Collection, out ScrValue collection) || collection.IsUnknown )
        {
            return;
        }

        // MustBe, so a value that MIGHT be an array is left alone. Undefined is excluded from the
        // test rather than reported: enumerating an unassigned name is the unassigned-variable
        // rule's finding, not this one's.
        ScrTypeSet scalars = ScrTypeSet.Int | ScrTypeSet.Float | ScrTypeSet.Bool
            | ScrTypeSet.AnyString | ScrTypeSet.Vector | ScrTypeSet.Function;

        if ( !collection.Without(ScrTypeSet.Undefined).MustBe(scalars) )
        {
            return;
        }

        diagnostics.Add(Diagnostic.Create(
            foreachNode.Collection.Range,
            DiagnosticSeverity.Warning,
            GscDiagnosticCode.CannotEnumerateType,
            ScrValues.Describe(collection.Types)));
    }

    /// <summary>
    /// A vector component that is certainly not a number. <c>( 0, 0, 1 )</c> is the commonest
    /// expression in the corpora, so this one has to be exact or it is unusable.
    /// </summary>
    private static void InspectVectorComponents(
        VectorNode vector, ScriptTypes types, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        Inspect(vector.X, types, diagnostics);
        Inspect(vector.Y, types, diagnostics);
        Inspect(vector.Z, types, diagnostics);

        static void Inspect(
            ExprNode component, ScriptTypes types, ImmutableArray<Diagnostic>.Builder diagnostics)
        {
            if ( !types.TryGetValue(component, out ScrValue value) || value.IsUnknown )
            {
                return;
            }

            // A bool is 0 or 1, so it is a number here.
            if ( value.MayBe(ScrTypeSet.Number | ScrTypeSet.Bool | ScrTypeSet.Undefined) )
            {
                return;
            }

            diagnostics.Add(Diagnostic.Create(
                component.Range,
                DiagnosticSeverity.Warning,
                GscDiagnosticCode.InvalidVectorComponent,
                ScrValues.Describe(value.Types)));
        }
    }

}
