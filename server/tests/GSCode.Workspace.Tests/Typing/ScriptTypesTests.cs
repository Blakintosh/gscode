using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;
using GSCode.Workspace.Api;
using GSCode.Workspace.Typing;
using Xunit;

namespace GSCode.Workspace.Tests.Typing;

/// <summary>
/// The per-node query surface, which is what a rewriter consumes.
///
/// The editor surfaces ask about one position or one assignment site. A transpiler walks the tree
/// it is translating and has to ask about every node it passes, including the ones nothing was ever
/// reported about — so the map has to be complete and it has to be keyed by identity.
/// </summary>
public class ScriptTypesTests
{
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static FlowTyper NewTyper()
    {
        return new FlowTyper(ApiLoader.Load(ApiDirectory, ScriptLanguage.Gsc), ObjectFields.Load(ApiDirectory));
    }

    private static ParseResult Parse(string body)
    {
        string source = "function f( a )\n{\n" + body + "\n}\n";

        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        Assert.DoesNotContain(result.AllDiagnostics, d => (int)d.Code is >= 3000 and < 4000);
        return result;
    }

    /// <summary>Finds the first node of a kind, by walking the tree the way a rewriter would.</summary>
    private static T FirstOf<T>(AstNode node) where T : ExprNode
    {
        return TryFirstOf<T>(node) ?? throw new InvalidOperationException($"no {typeof(T).Name} in the tree");
    }

    private static T? TryFirstOf<T>(AstNode node) where T : ExprNode
    {
        if ( node is T match )
        {
            return match;
        }

        foreach ( AstNode child in AstSearch.ChildrenOf(node) )
        {
            T? found = TryFirstOf<T>(child);
            if ( found is not null )
            {
                return found;
            }
        }

        return null;
    }

    [Fact]
    public void EveryWalkedExpressionHasAValue()
    {
        ScriptTypes types = NewTyper().InferValues(Parse("    x = 1 + 2;\n    y = \"text\";"));

        Assert.True(types.Count > 0);
    }

    [Fact]
    public void ANodeCanBeAskedAboutDirectly()
    {
        ParseResult result = Parse("    x = ( 0, 0, 1 );");
        ScriptTypes types = NewTyper().InferValues(result);

        VectorNode vector = FirstOf<VectorNode>(result.Tree.Root);

        Assert.True(types.TryGetValue(vector, out ScrValue value));
        Assert.Equal(ScrTypeSet.Vector, value.Types);
    }

    [Fact]
    public void TwoIdenticalLiteralsAreSeparateEntries()
    {
        // The reason the map is keyed by REFERENCE. Every AST node is a record, so structural
        // equality would make the three zeroes in `( 0, 0, 0 )` one key — and a rewriter asking
        // about the second would be answered about the first.
        ParseResult result = Parse("    v = ( 0, 0, 0 );");
        ScriptTypes types = NewTyper().InferValues(result);

        VectorNode vector = FirstOf<VectorNode>(result.Tree.Root);

        Assert.NotSame(vector.X, vector.Y);
        Assert.True(types.TryGetValue(vector.X, out _));
        Assert.True(types.TryGetValue(vector.Y, out _));
        Assert.True(types.TryGetValue(vector.Z, out _));
    }

    [Fact]
    public void AConstantSurvivesIntoTheMap()
    {
        ParseResult result = Parse("    x = 40 + 2;");
        ScriptTypes types = NewTyper().InferValues(result);

        BinaryNode sum = FirstOf<BinaryNode>(result.Tree.Root);

        Assert.True(types.TryGetValue(sum, out ScrValue value));
        Assert.Equal(42L, value.Constant!.Value.Integer);
    }

    [Fact]
    public void AnUnaskedNodeAnswersUnknownRatherThanThrowing()
    {
        ScriptTypes types = ScriptTypes.Empty;
        ParseResult result = Parse("    x = 1;");

        Assert.True(types.ValueOf(FirstOf<LiteralNode>(result.Tree.Root)).IsUnknown);
    }

    [Fact]
    public void TheAssignmentAndFieldWriteListsComeAlong()
    {
        // The same lists the editor surfaces read, so a caller needs only one pass for both.
        ScriptTypes types = NewTyper().InferValues(Parse("    x = 1;\n    self.count = 2;"));

        Assert.NotEmpty(types.Assignments);
        Assert.NotEmpty(types.FieldWrites);
    }

    [Fact]
    public void ImprecisionIsCountedByReason()
    {
        // The coverage number a transpiler is budgeted against: not merely how much is unknown, but
        // which unknown to attack next.
        ScriptTypes types = NewTyper().InferValues(Parse("    x = a;\n    y = a[ 0 ];\n    z = 1;"));

        Dictionary<ScrImprecision, int> histogram = types.ImprecisionHistogram();

        Assert.True(histogram.ContainsKey(ScrImprecision.UntypedParameter));
        Assert.True(histogram.ContainsKey(ScrImprecision.ArrayElement));
        Assert.True(histogram.ContainsKey(ScrImprecision.None));
    }

    [Fact]
    public void AParameterIsKnownToBeAParameter()
    {
        // Parameters were seeded only for the hover pass, so the hint pass could not tell an
        // assignment to a parameter from one to a fresh local.
        //
        // The READ of `a` is the node in the map: an assignment's target is written, not evaluated,
        // so it is never typed — which is itself worth knowing when reading this map.
        ParseResult result = Parse("    x = a;");
        ScriptTypes types = NewTyper().InferValues(result);

        IdentifierNode read = IdentifierNamed(result.Tree.Root, "a");

        Assert.True(types.TryGetValue(read, out ScrValue value));
        Assert.Equal(ScrImprecision.UntypedParameter, value.Imprecision);
    }

    [Fact]
    public void AnAssignmentTargetIsNotEvaluatedSoItIsNotInTheMap()
    {
        // The counterpart to the test above, stated so a reader of the map is not surprised by it.
        ParseResult result = Parse("    x = a;");
        ScriptTypes types = NewTyper().InferValues(result);

        Assert.False(types.TryGetValue(IdentifierNamed(result.Tree.Root, "x"), out _));
    }

    private static IdentifierNode IdentifierNamed(AstNode node, string name)
    {
        return TryIdentifierNamed(node, name)
            ?? throw new InvalidOperationException($"no identifier named '{name}'");
    }

    /// <summary>
    /// Returns null for "not in this subtree" rather than throwing and catching per child, which is
    /// what the first version did — an exception per node not containing the name, on every level of
    /// a recursive walk.
    /// </summary>
    private static IdentifierNode? TryIdentifierNamed(AstNode node, string name)
    {
        if ( node is IdentifierNode identifier
            && string.Equals(identifier.Token.Text, name, StringComparison.Ordinal) )
        {
            return identifier;
        }

        foreach ( AstNode child in AstSearch.ChildrenOf(node) )
        {
            IdentifierNode? found = TryIdentifierNamed(child, name);
            if ( found is not null )
            {
                return found;
            }
        }

        return null;
    }

    [Fact]
    public void TheRicherValueIsAvailableAtAPosition()
    {
        // TryGetLocalTypeAt gives an editor its coarse label; this gives a rewriter the union and
        // the reason behind it.
        ParseResult result = Parse("    if ( a )\n    {\n        v = 1;\n    }\n    else\n    {\n        v = \"text\";\n    }\n    use( v );");

        // On the `v` inside `use( v )`. The body starts at line 2 of the wrapped source, so the
        // nine body lines run 2..10 and the call is the last of them.
        Position position = new(10, 9);

        Assert.True(NewTyper().TryGetValueAt(result, position, out ScrValue value));
        Assert.Equal(ScrTypeSet.Int | ScrTypeSet.String, value.Types);

        // The coarse projection still says Unknown, which is what the editor should show.
        Assert.Equal(ScrType.Unknown, value.ToScrType());
    }

    [Fact]
    public void RecordingIsOffForTheOrdinaryPasses()
    {
        // The hint and hover passes ask about one name or one position, and must not pay for a whole
        // file's map to answer it. Two passes on one instance must not interfere either.
        FlowTyper typer = NewTyper();
        ParseResult result = Parse("    x = 1;");

        ScriptTypes first = typer.InferValues(result);
        typer.InferAssignments(result);
        ScriptTypes second = typer.InferValues(result);

        Assert.Equal(first.Count, second.Count);
    }
}
