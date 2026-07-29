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

public class FlowTyperTests
{
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static FlowTyper NewTyper()
    {
        return new FlowTyper(ApiLoader.Load(ApiDirectory, ScriptLanguage.Gsc), ObjectFields.Load(ApiDirectory));
    }

    private static Dictionary<string, ScrType> InferByFirstToken(string body)
    {
        string source = "function f()\n{\n" + body + "\n}\n";
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        FlowTyper typer = NewTyper();
        ImmutableArray<InferredAssignment> inferred = typer.InferAssignments(result);

        // Key each inferred type by the source text under its name range for easy asserting.
        Dictionary<string, ScrType> byName = new(StringComparer.Ordinal);
        foreach ( InferredAssignment assignment in inferred )
        {
            int offset = result.Text.GetOffset(assignment.NameRange.Start);
            int end = result.Text.GetOffset(assignment.NameRange.End);
            byName[source[offset..end]] = assignment.Type;
        }

        return byName;
    }

    [Fact]
    public void AssignmentsInsideADevBlockAreTyped()
    {
        // `/# … #/` is real code that runs in a debug build. FlowTyper's walk had no case for the
        // node at all, so nothing inside one was ever visited — no inlay, no hover type, and
        // nothing for the field lints to see.
        Dictionary<string, ScrType> types = InferByFirstToken(
            "    /#\n        debugCount = 5;\n        level.debugName = \"x\";\n    #/");

        Assert.Equal(ScrType.Int, types["debugCount"]);
        Assert.Equal(ScrType.String, types["debugName"]);
    }

    [Fact]
    public void ADevBlockLocalIsNotAssumedToExistAfterIt()
    {
        // The block is compiled out of a release build, so code after it cannot assume anything it
        // assigned still holds — the same treatment a loop body gets for the same reason.
        string source = "function f()\n{\n\t/#\n\tn = 5;\n\t#/\n\n\tuse( n );\n}\n";

        Assert.False(HoverAt(source, new Position(6, 6), out _));
    }

    [Fact]
    public void InsideADevBlockTheAssignmentHolds()
    {
        string source = "function f()\n{\n\t/#\n\tn = 5;\n\tuse( n );\n\t#/\n}\n";

        Assert.True(HoverAt(source, new Position(4, 6), out LocalTypeHover hover));
        Assert.Equal(ScrType.Int, hover.Type);
    }

    [Fact]
    public void FieldAssignments_AreTypedLikeLocals()
    {
        // The reported gap: `foo = "lol"` showed its <string> hint and `level.foo = "lol"` showed
        // nothing, purely because the field branch returned before any hint was recorded.
        Dictionary<string, ScrType> types = InferByFirstToken(
            "    level.name = \"lol\";\n    self.count = 5;\n    level.on = true;\n    a.b.deep = 1.5;");

        Assert.Equal(ScrType.String, types["name"]);
        Assert.Equal(ScrType.Int, types["count"]);
        Assert.Equal(ScrType.Bool, types["on"]);
        Assert.Equal(ScrType.Float, types["deep"]);
    }

    [Fact]
    public void AFieldTakesTheAssignedValuesType_NotTheEngineDatas()
    {
        // `origin` is an engine field the data types as a vector. Reading the type back through the
        // field data would report that instead of what was actually assigned - and would say
        // nothing at all about the invented fields, which are most of them.
        Dictionary<string, ScrType> types = InferByFirstToken("    level.origin = \"a string\";");

        Assert.Equal(ScrType.String, types["origin"]);
    }

    [Fact]
    public void TheSameFieldNameOnDifferentOwnersIsHintedSeparately()
    {
        // Keyed by the whole path, so hinting `self.count` does not suppress `level.count`. Both
        // are first-for-name, which is what the inlay surface filters on.
        string source = "function f()\n{\n    self.count = 1;\n    level.count = 2;\n}\n";
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        ImmutableArray<InferredAssignment> inferred = NewTyper().InferAssignments(result);

        Assert.Equal(2, inferred.Count(a => a.Name == "count" && a.IsFirstForName));
    }

    [Fact]
    public void Literals_AreTyped()
    {
        Dictionary<string, ScrType> types = InferByFirstToken("    a = 5;\n    b = 3.14;\n    c = \"hi\";\n    d = true;\n    v = ( 0, 0, 0 );\n    ls = &\"MENU\";");

        Assert.Equal(ScrType.Int, types["a"]);
        Assert.Equal(ScrType.Float, types["b"]);
        Assert.Equal(ScrType.String, types["c"]);
        Assert.Equal(ScrType.Bool, types["d"]);
        Assert.Equal(ScrType.Vector, types["v"]);
        Assert.Equal(ScrType.IString, types["ls"]);
    }

    [Fact]
    public void Arithmetic_WidensAndConcatenates()
    {
        Dictionary<string, ScrType> types = InferByFirstToken("    i = 1 + 2;\n    f = 1 + 2.0;\n    s = \"a\" + 1;\n    cmp = 1 < 2;");

        Assert.Equal(ScrType.Int, types["i"]);
        Assert.Equal(ScrType.Float, types["f"]);
        Assert.Equal(ScrType.String, types["s"]);
        Assert.Equal(ScrType.Bool, types["cmp"]);
    }

    [Fact]
    public void EnvironmentThreadsEarlierLocals()
    {
        Dictionary<string, ScrType> types = InferByFirstToken("    a = 5;\n    b = a + 1;");
        Assert.Equal(ScrType.Int, types["b"]);
    }

    [Fact]
    public void Globals_AreTyped()
    {
        Dictionary<string, ScrType> types = InferByFirstToken("    e = self;\n    l = level;\n    g = game;");
        Assert.Equal(ScrType.Entity, types["e"]);
        Assert.Equal(ScrType.Struct, types["l"]);
        Assert.Equal(ScrType.Array, types["g"]);
    }

    [Fact]
    public void BuiltinReturn_IsTyped()
    {
        // Abs returns float per the API.
        Dictionary<string, ScrType> types = InferByFirstToken("    x = Abs( -3.0 );");
        Assert.Equal(ScrType.Float, types["x"]);
    }

    [Fact]
    public void CallableKeywords_AreTypedByTheEmulationTable()
    {
        // isdefined and vectorscale are keywords with no builtin-API entry, so without the
        // emulation table they would type as Unknown and produce no hint at all.
        Dictionary<string, ScrType> types = InferByFirstToken(
            "    ok = isdefined( self );\n    scaled = vectorscale( ( 0, 0, 1 ), 5 );");

        Assert.Equal(ScrType.Bool, types["ok"]);
        Assert.Equal(ScrType.Vector, types["scaled"]);
    }

    [Fact]
    public void StatementShapedKeywords_StillProduceNoHint()
    {
        // profilestart yields no value, so it is deliberately absent from the table.
        Dictionary<string, ScrType> types = InferByFirstToken("    nothing = profilestart( \"x\" );");

        Assert.False(types.ContainsKey("nothing"));
    }

    [Fact]
    public void UnknownExpressions_ProduceNoHint()
    {
        // A call to an unknown function is Unknown -> no hint recorded.
        Dictionary<string, ScrType> types = InferByFirstToken("    x = mystery_function();");
        Assert.False(types.ContainsKey("x"));
    }

    [Fact]
    public void EveryAssignmentIsRecorded_ButOnlyTheFirstIsHinted()
    {
        // Both are in the list, because hover needs the later one to report the type as of the
        // cursor. IsFirstForName is what inlay hints filter on, so the `: int` label appears once
        // rather than at every reassignment.
        string source = "function f()\n{\n    a = 1;\n    a = \"now a string\";\n}\n";
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        ImmutableArray<InferredAssignment> assignments = NewTyper().InferAssignments(result);
        InferredAssignment[] toA = [.. assignments.Where(a => a.Name == "a")];

        Assert.Equal(2, toA.Length);
        Assert.Equal(ScrType.Int, toA[0].Type);
        Assert.True(toA[0].IsFirstForName);
        Assert.Equal(ScrType.String, toA[1].Type);
        Assert.False(toA[1].IsFirstForName);
    }

    [Fact]
    public void SizeProperty_IsInt()
    {
        Dictionary<string, ScrType> types = InferByFirstToken("    players = level.players;\n    n = players.size;");
        Assert.Equal(ScrType.Int, types["n"]);
    }

    [Fact]
    public void HoverLookup_ReturnsLocalType_AtUsageSite()
    {
        string source = "function f()\n{\n    count = 5;\n    other = count;\n}\n";
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());
        FlowTyper typer = NewTyper();

        // Position on 'count' where it is READ in `other = count;` (line 3, char 12).
        bool found = typer.TryGetLocalTypeAt(result, new Position(3, 12), out LocalTypeHover hover);

        Assert.True(found);
        Assert.Equal("count", hover.Name);
        Assert.Equal(ScrType.Int, hover.Type);
    }

    [Fact]
    public void HoverLookup_ReturnsFalse_ForUntypedParameter()
    {
        string source = "function f( amount )\n{\n    use( amount );\n}\n";
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());
        FlowTyper typer = NewTyper();

        // 'amount' is a parameter, never assigned a concrete type -> no hover.
        bool found = typer.TryGetLocalTypeAt(result, new Position(2, 9), out LocalTypeHover hover);

        Assert.False(found);
    }

    // --- Branches: the environment at the cursor, not the last arm written ---

    private static bool HoverAt(string source, Position position, out LocalTypeHover hover)
    {
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        return NewTyper().TryGetLocalTypeAt(result, position, out hover);
    }

    [Fact]
    public void AfterAnIfElse_DisagreeingArmsJoinRatherThanTakingTheLastOne()
    {
        // The reported gap. Reading the hint list reported `string` here, because a hint records
        // what a name became at one assignment SITE and no site represents the join of two
        // branches that have both already run. The walk always computed the join; hover simply
        // never looked at it.
        string source =
            "function f( c )\n{\n\tif ( c )\n\t{\n\t\tx = 1;\n\t}\n\telse\n\t{\n\t\tx = \"s\";\n\t}\n\n\tuse( x );\n}\n";

        // `x` inside use(), after the whole if/else.
        Assert.False(HoverAt(source, new Position(11, 6), out _));
    }

    [Fact]
    public void AfterAnIfElse_AgreeingArmsKeepTheirType()
    {
        // The join only moves toward Unknown when the arms actually disagree.
        string source =
            "function f( c )\n{\n\tif ( c )\n\t{\n\t\tx = 1;\n\t}\n\telse\n\t{\n\t\tx = 2;\n\t}\n\n\tuse( x );\n}\n";

        Assert.True(HoverAt(source, new Position(11, 6), out LocalTypeHover hover));
        Assert.Equal(ScrType.Int, hover.Type);
    }

    [Fact]
    public void InsideAnArm_TheArmsOwnTypeIsReported()
    {
        // A cursor inside one arm is ON that path: the other arm has not run, so joining it in
        // would report a type the code at the cursor cannot see.
        string source =
            "function f( c )\n{\n\tif ( c )\n\t{\n\t\tx = 1;\n\t\tuse( x );\n\t}\n\telse\n\t{\n\t\tx = \"s\";\n\t}\n}\n";

        Assert.True(HoverAt(source, new Position(5, 7), out LocalTypeHover hover));
        Assert.Equal(ScrType.Int, hover.Type);
    }

    [Fact]
    public void InsideALoopBody_TheBodyHasRun()
    {
        // The zero-iteration alternative is not a possibility the code inside the body allows for.
        string source =
            "function f( items )\n{\n\tforeach ( item in items )\n\t{\n\t\tn = 5;\n\t\tuse( n );\n\t}\n}\n";

        Assert.True(HoverAt(source, new Position(5, 7), out LocalTypeHover hover));
        Assert.Equal(ScrType.Int, hover.Type);
    }

    [Fact]
    public void AfterALoop_TheBodyMightNotHaveRun()
    {
        // Outside it, the loop may have run zero times, so a name typed only inside it joins with
        // the environment as it stood before - and becomes Unknown.
        string source =
            "function f( items )\n{\n\tforeach ( item in items )\n\t{\n\t\tn = 5;\n\t}\n\n\tuse( n );\n}\n";

        Assert.False(HoverAt(source, new Position(7, 6), out _));
    }

    // --- The type AT the cursor, not the type it started as ---

    private static ParseResult Reassigned()
    {
        // count is an int, then a string, then read once more.
        string source = "function f()\n{\n    count = 5;\n    a = count;\n    count = \"hello\";\n    b = count;\n}\n";
        return ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());
    }

    [Fact]
    public void HoverLookup_UsesTheAssignmentAboveTheCursor()
    {
        // The read on line 3 sits between the two assignments, so it is still an int.
        FlowTyper typer = NewTyper();

        Assert.True(typer.TryGetLocalTypeAt(Reassigned(), new Position(3, 8), out LocalTypeHover hover));
        Assert.Equal(ScrType.Int, hover.Type);
    }

    [Fact]
    public void HoverLookup_FollowsAReassignmentToADifferentType()
    {
        // The reported regression: the read on line 5 used to report int, because the lookup
        // returned the FIRST assignment in the function rather than the one above the cursor.
        FlowTyper typer = NewTyper();

        Assert.True(typer.TryGetLocalTypeAt(Reassigned(), new Position(5, 8), out LocalTypeHover hover));
        Assert.Equal(ScrType.String, hover.Type);
    }

    [Fact]
    public void HoverLookup_IgnoresAssignmentsBelowTheCursor()
    {
        // Hovering the name in `count = 5;` itself: the string assignment further down says
        // nothing about the value here.
        FlowTyper typer = NewTyper();

        Assert.True(typer.TryGetLocalTypeAt(Reassigned(), new Position(2, 5), out LocalTypeHover hover));
        Assert.Equal(ScrType.Int, hover.Type);
    }
}
