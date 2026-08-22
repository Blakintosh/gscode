using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Api;
using GSCode.Workspace.Typing;
using Xunit;

namespace GSCode.Workspace.Tests.Typing;

/// <summary>
/// Parameter types read off the arguments callers pass.
///
/// This is the gap that actually blocks a dialect transpiler. Whether an array parameter is mutated
/// by its callee is the only behavioural difference between Black Ops III and the earlier games, so
/// "is this parameter an array" has to be answerable — and a function typed in isolation can never
/// answer it.
/// </summary>
public class ParameterTypesTests
{
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static ImmutableDictionary<string, ImmutableArray<ScrValue>> Infer(string source)
    {
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        Assert.DoesNotContain(result.AllDiagnostics, d => (int)d.Code is >= 3000 and < 4000);

        FlowTyper typer = new(ApiLoader.Load(ApiDirectory, ScriptLanguage.Gsc), ObjectFields.Load(ApiDirectory));
        return ParameterTypes.Infer(result, typer);
    }

    [Fact]
    public void AParameterTakesTheTypeItsCallerPasses()
    {
        ImmutableDictionary<string, ImmutableArray<ScrValue>> inferred = Infer(
            "function helper( n )\n{\n}\nfunction caller()\n{\n    helper( 5 );\n}\n");

        Assert.True(inferred.TryGetValue("helper", out ImmutableArray<ScrValue> parameters));
        Assert.Single(parameters);
        Assert.Equal(ScrTypeSet.Int, parameters[0].Types);
    }

    [Fact]
    public void AnArrayArgumentIsRecognised()
    {
        // The case the whole exercise exists for: an array parameter is the one thing whose pass
        // semantics differ between dialects.
        ImmutableDictionary<string, ImmutableArray<ScrValue>> inferred = Infer(
            "function fill( items )\n{\n}\nfunction caller()\n{\n    fill( [] );\n}\n");

        Assert.True(inferred["fill"][0].MustBe(ScrTypeSet.Array));
    }

    [Fact]
    public void AStructArgumentIsNotMistakenForAnArray()
    {
        // The control in the direction that matters. A struct parameter translates safely in either
        // direction; calling it an array would flag work that does not need doing, and the reverse
        // would miss work that does.
        ImmutableDictionary<string, ImmutableArray<ScrValue>> inferred = Infer(
            "function use_it( s )\n{\n}\nfunction caller()\n{\n    use_it( spawnstruct() );\n}\n");

        Assert.True(inferred["use_it"][0].MustBe(ScrTypeSet.Struct));
        Assert.False(inferred["use_it"][0].MayBe(ScrTypeSet.Array));
    }

    [Fact]
    public void TwoCallersWideningTheParameterProduceAUnion()
    {
        // The function must accept both, so the second caller widens rather than replaces.
        ImmutableDictionary<string, ImmutableArray<ScrValue>> inferred = Infer(
            "function helper( v )\n{\n}\nfunction caller()\n{\n    helper( 5 );\n    helper( \"text\" );\n}\n");

        Assert.Equal(ScrTypeSet.Int | ScrTypeSet.String, inferred["helper"][0].Types);
    }

    [Fact]
    public void AnAmbiguousArrayParameterIsDistinguishableFromABothDecidedOne()
    {
        // The three-way answer. A parameter passed an array by one caller and a struct by another
        // cannot be decided, and a transpiler has to escalate it rather than pick.
        ImmutableDictionary<string, ImmutableArray<ScrValue>> inferred = Infer(
            "function helper( v )\n{\n}\nfunction caller()\n{\n    helper( [] );\n    helper( spawnstruct() );\n}\n");

        ScrValue parameter = inferred["helper"][0];

        Assert.True(parameter.MayBe(ScrTypeSet.Array));
        Assert.False(parameter.MustBe(ScrTypeSet.Array));
    }

    [Theory]
    [InlineData("    helper( 1, 2 );\n    helper( 1 );")]
    [InlineData("    helper( 1 );\n    helper( 1, 2 );")]
    public void ACallerPassingFewerArgumentsMakesTheRestMaybeUndefined(string calls)
    {
        // Legal in GSC — the missing ones are undefined — and precisely the kind of fact a rewriter
        // must not lose.
        //
        // BOTH orders, because the first version of this test asserted only the second one and
        // passed while the code was wrong: omission was folded in when a later call was SHORTER but
        // not when it was longer, so `helper( 1 ); helper( 1, 2 );` reported the second parameter as
        // a plain int. One order proving a symmetric rule is the failure mode this pins.
        ImmutableDictionary<string, ImmutableArray<ScrValue>> inferred = Infer(
            "function helper( a, b )\n{\n}\nfunction caller()\n{\n" + calls + "\n}\n");

        Assert.True(inferred["helper"][1].MayBe(ScrTypeSet.Undefined));
        Assert.True(inferred["helper"][1].MayBe(ScrTypeSet.Int));
    }

    [Fact]
    public void TheOrderOfCallSitesDoesNotChangeTheAnswer()
    {
        // Stated directly as well, since order dependence is the class of bug rather than one case
        // of it: whatever the callers say, they say it whichever way round they are written.
        ImmutableDictionary<string, ImmutableArray<ScrValue>> forward = Infer(
            "function helper( a, b )\n{\n}\nfunction caller()\n{\n    helper( 1 );\n    helper( 1, \"x\" );\n}\n");

        ImmutableDictionary<string, ImmutableArray<ScrValue>> reversed = Infer(
            "function helper( a, b )\n{\n}\nfunction caller()\n{\n    helper( 1, \"x\" );\n    helper( 1 );\n}\n");

        Assert.Equal(forward["helper"][0].Types, reversed["helper"][0].Types);
        Assert.Equal(forward["helper"][1].Types, reversed["helper"][1].Types);
    }

    [Fact]
    public void SeveralPositionsAreTrackedSeparately()
    {
        ImmutableDictionary<string, ImmutableArray<ScrValue>> inferred = Infer(
            "function helper( a, b, c )\n{\n}\nfunction caller()\n{\n    helper( 1, \"text\", [] );\n}\n");

        Assert.Equal(ScrTypeSet.Int, inferred["helper"][0].Types);
        Assert.Equal(ScrTypeSet.String, inferred["helper"][1].Types);
        Assert.Equal(ScrTypeSet.Array, inferred["helper"][2].Types);
    }

    [Fact]
    public void AnArgumentTheFlowCannotTypeIsNotGuessed()
    {
        // Passing one function's own parameter through to another. The second pass could chase this
        // with another round, and deliberately does not — the value is unknown, and it says so.
        ImmutableDictionary<string, ImmutableArray<ScrValue>> inferred = Infer(
            "function inner( v )\n{\n}\nfunction outer( passed )\n{\n    inner( passed );\n}\n");

        Assert.True(inferred["inner"][0].IsUnknown);
    }

    [Fact]
    public void AnAlreadyComputedMapGivesTheSameAnswer()
    {
        // A rewriter wants both the per-node values and the parameter signatures. Without this
        // overload it would build a whole file's map, then hand the file back to be walked and
        // mapped a second time to read the arguments out of it.
        string source = "function helper( n )\n{\n}\nfunction caller()\n{\n    helper( 5 );\n}\n";

        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        FlowTyper typer = new(ApiLoader.Load(ApiDirectory, ScriptLanguage.Gsc), ObjectFields.Load(ApiDirectory));

        ScriptTypes typed = typer.InferValues(result);
        ImmutableDictionary<string, ImmutableArray<ScrValue>> fromMap = ParameterTypes.Infer(result, typed);
        ImmutableDictionary<string, ImmutableArray<ScrValue>> fromTyper = ParameterTypes.Infer(result, typer);

        Assert.Equal(fromTyper["helper"][0].Types, fromMap["helper"][0].Types);
        Assert.Equal(ScrTypeSet.Int, fromMap["helper"][0].Types);
    }

    [Fact]
    public void AFunctionNothingCallsIsAbsentRatherThanClaimed()
    {
        ImmutableDictionary<string, ImmutableArray<ScrValue>> inferred = Infer(
            "function orphan( v )\n{\n}\n");

        Assert.False(inferred.ContainsKey("orphan"));
    }

    [Fact]
    public void AMethodCallIsCountedToo()
    {
        // `self helper( x )` is the same function reached the other way.
        ImmutableDictionary<string, ImmutableArray<ScrValue>> inferred = Infer(
            "function helper( n )\n{\n}\nfunction caller()\n{\n    self helper( 5 );\n}\n");

        Assert.Equal(ScrTypeSet.Int, inferred["helper"][0].Types);
    }

    [Fact]
    public void ConstantsSurviveIntoTheParameter()
    {
        // A folded value, not merely a type — which is what lets a rewriter specialise a call.
        ImmutableDictionary<string, ImmutableArray<ScrValue>> inferred = Infer(
            "function helper( n )\n{\n}\nfunction caller()\n{\n    helper( 40 + 2 );\n}\n");

        Assert.Equal(42L, inferred["helper"][0].Constant!.Value.Integer);
    }
}
