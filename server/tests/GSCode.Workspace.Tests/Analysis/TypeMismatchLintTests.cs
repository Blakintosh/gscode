using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Analysis;
using GSCode.Workspace.Api;
using GSCode.Workspace.Typing;
using Xunit;

namespace GSCode.Workspace.Tests.Analysis;

/// <summary>
/// Two rules the union lattice made answerable, and both report NOTHING across all five corpora —
/// so the controls below are the only thing standing between them and being silently broken.
///
/// A third, `OperatorNotSupportedOnTypes`, was written alongside these and withdrawn at 752 findings
/// on shipped code. Its two failure modes are pinned here as well, because both are traps the next
/// type rule can fall into: a guard that tests `IsUnknown` misses a value narrowed by `isdefined`,
/// and the operator table is stricter about `vector + scalar` than the engine is.
/// </summary>
public class TypeMismatchLintTests
{
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static ImmutableArray<Diagnostic> Lint(string body)
    {
        string source = "function f( a )\n{\n" + body + "\n}\n";

        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        Assert.DoesNotContain(result.AllDiagnostics, d => (int)d.Code is >= 3000 and < 4000);

        FlowTyper typer = new(ApiLoader.Load(ApiDirectory, ScriptLanguage.Gsc), ObjectFields.Load(ApiDirectory));
        return TypeMismatchLint.Analyze(result, typer);
    }

    // --- 5033: enumerating something that cannot be enumerated ---

    [Theory]
    [InlineData("    n = 5;\n    foreach ( x in n )\n    {\n    }")]
    [InlineData("    s = \"text\";\n    foreach ( x in s )\n    {\n    }")]
    [InlineData("    v = ( 0, 0, 1 );\n    foreach ( x in v )\n    {\n    }")]
    [InlineData("    b = true;\n    foreach ( x in b )\n    {\n    }")]
    public void EnumeratingAScalarIsReported(string body)
    {
        // The controls. Both rules in this file report zero over every corpus, so without these
        // there is nothing to distinguish "correctly silent" from "does not work".
        Assert.Single(Lint(body), d => d.Code == GscDiagnosticCode.CannotEnumerateType);
    }

    [Theory]
    [InlineData("    items = [];\n    foreach ( x in items )\n    {\n    }")]
    [InlineData("    s = spawnstruct();\n    foreach ( x in s )\n    {\n    }")]
    [InlineData("    foreach ( x in a )\n    {\n    }")]
    [InlineData("    foreach ( x in level.things )\n    {\n    }")]
    [InlineData("    foreach ( x in game )\n    {\n    }")]
    public void EnumeratingAnythingElseIsLeftAlone(string body)
    {
        // A struct IS enumerable, an untyped parameter says nothing, and a field the engine data
        // does not know says nothing either.
        Assert.Empty(Lint(body));
    }

    [Fact]
    public void AValueThatMightBeAnArrayIsNotReported()
    {
        // MustBe, not MayBe. One arm assigning an array is enough to make the rule decline — which
        // is the whole difference between this and the flat lattice, where the join said Unknown
        // and the rule had nothing to reason about either way.
        Assert.Empty(Lint(
            "    if ( a )\n    {\n        c = [];\n    }\n    else\n    {\n        c = 5;\n    }\n"
            + "    foreach ( x in c )\n    {\n    }"));
    }

    [Fact]
    public void EnumeratingSomethingCertainlyUndefinedIsReported()
    {
        // The gap the first version of this rule had. `foo = undefined;` is not a scalar, so a
        // "is it certainly a scalar" test declined — and 5016 stays quiet because the name genuinely
        // WAS assigned, so nothing reported it at all. Asking "could this possibly be enumerable"
        // instead catches it, and undefined falls out as one more thing that cannot be.
        Assert.Single(
            Lint("    foo = undefined;\n    foreach ( ent in foo )\n    {\n    }"),
            d => d.Code == GscDiagnosticCode.CannotEnumerateType);
    }

    [Fact]
    public void AWaittillReboundNameLosesItsOldType()
    {
        // `self waittill( "evt", x )` BINDS x — an output the engine fills in, the same convention
        // UnassignedVariableLint honours. Reusing a name across a wait is ordinary GSC, so the
        // string assigned before the wait must not survive it; holding x to its old type made this
        // exact shape a false-positive 5033.
        Assert.Empty(Lint(
            "    x = \"hello\";\n    self waittill( \"evt\", x );\n    foreach ( i in x )\n    {\n    }"));
    }

    [Fact]
    public void TheWaittillEventNameIsStillARead()
    {
        // Only the TRAILING arguments are bound. The first is the event name, a genuine read, so a
        // scalar used there keeps its type and enumerating it afterwards is still reported.
        Assert.Single(
            Lint("    e = 5;\n    self waittill( e );\n    foreach ( i in e )\n    {\n    }"),
            d => d.Code == GscDiagnosticCode.CannotEnumerateType);
    }

    [Fact]
    public void AnUnassignedNameIsTheOtherRulesFinding()
    {
        // The case the rule above must NOT swallow. Assigned on one branch only is `array|undefined`
        // — it MIGHT be enumerable, so it belongs to 5016, and two diagnostics on one range for one
        // mistake is the failure this avoids.
        Assert.Empty(Lint("    if ( a )\n    {\n        c = [];\n    }\n    foreach ( x in c )\n    {\n    }"));
    }

    // --- 5034: a vector component that cannot be a number ---

    [Theory]
    [InlineData("    v = ( \"x\", 0, 1 );")]
    [InlineData("    v = ( 0, \"y\", 1 );")]
    [InlineData("    v = ( 0, 1, \"z\" );")]
    public void AStringVectorComponentIsReported(string body)
    {
        Assert.Single(Lint(body), d => d.Code == GscDiagnosticCode.InvalidVectorComponent);
    }

    [Fact]
    public void AnArrayComponentIsReported()
    {
        Assert.Single(
            Lint("    items = [];\n    v = ( items, 0, 1 );"),
            d => d.Code == GscDiagnosticCode.InvalidVectorComponent);
    }

    [Theory]
    [InlineData("    v = ( 0, 0, 1 );")]
    [InlineData("    v = ( 0.5, -1, 2.25 );")]
    [InlineData("    v = ( a, a, a );")]
    [InlineData("    n = 4;\n    v = ( n, n, n );")]
    [InlineData("    v = ( true, 0, 1 );")]
    [InlineData("    v = ( self.origin[ 0 ], 0, 1 );")]
    public void TheShapesTheStockScriptsUseAreAccepted(string body)
    {
        // `( 0, 0, 1 )` is the commonest expression in the corpora — this rule has to be exact or it
        // is unusable. A bool is 0 or 1, an untyped parameter says nothing, and an indexed read is
        // an array element whose type is not modelled.
        Assert.Empty(Lint(body));
    }

    // --- the withdrawn rule's traps, pinned so the next one does not repeat them ---

    [Fact]
    public void AValueNarrowedByIsDefinedIsStillEffectivelyUnknown()
    {
        // The first trap. `IsUnknown` is exact equality with the universe, and isdefined narrowing
        // removes Undefined — leaving a value that knows nothing but no longer answers IsUnknown.
        // The withdrawn operator rule guarded on IsUnknown and reported the whole corpus.
        ScrValue narrowed = ScrValue.Unknown.Without(ScrTypeSet.Undefined);

        Assert.False(narrowed.IsUnknown);
        Assert.True(narrowed.MayBe(ScrTypeSet.Array));
        Assert.False(narrowed.MustBe(ScrTypeSet.Array));
    }

    [Fact]
    public void TheOperatorTableIsStricterThanTheEngineAboutVectorPlusScalar()
    {
        // The second trap, and the one that cannot be fixed by tightening a guard. The table calls
        // this unsupported; the stock scripts do it throughout. Anything diagnosing off the table
        // has to reckon with the table being wrong here first.
        ScrOperatorResult applied = ScrOperators.Apply(
            ScrBinaryOp.Add, ScrValue.Of(ScrTypeSet.Vector), ScrValue.Of(ScrTypeSet.Int));

        Assert.Equal(ScrOperandDiagnosis.UnsupportedOperands, applied.Diagnosis);
    }
}
