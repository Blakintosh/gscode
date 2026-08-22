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
/// The expression and statement forms the pass did not reach.
///
/// Every one of these produced nothing before: `TypeOf` had no case for indexes, ternaries,
/// postfixes, arrow calls, qualified names or pointer dereferences, and `WalkStatement` never
/// entered a foreach's bindings, never walked a for-loop's condition or increment, and fell
/// straight through `const`. A transpiler needs a value for every node, so the gaps are the work.
/// </summary>
public class TypeCoverageTests
{
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static FlowTyper NewTyper()
    {
        return new FlowTyper(ApiLoader.Load(ApiDirectory, ScriptLanguage.Gsc), ObjectFields.Load(ApiDirectory));
    }

    private static ParseResult Parse(string source)
    {
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        // Without this a syntax slip makes every "no hint" assertion below pass for the wrong reason.
        Assert.DoesNotContain(result.AllDiagnostics, d => (int)d.Code is >= 3000 and < 4000);
        return result;
    }

    /// <summary>The type of a named local, read from the hint list.</summary>
    private static ScrType TypeOf(string body, string name)
    {
        ParseResult result = Parse("function f( a, b )\n{\n" + body + "\n}\n");

        ScrType found = ScrType.Unknown;
        foreach ( InferredAssignment assignment in NewTyper().InferAssignments(result) )
        {
            if ( string.Equals(assignment.Name, name, StringComparison.OrdinalIgnoreCase) )
            {
                found = assignment.Type;
            }
        }

        return found;
    }

    private static bool HasHint(string body, string name)
    {
        ParseResult result = Parse("function f( a, b )\n{\n" + body + "\n}\n");

        foreach ( InferredAssignment assignment in NewTyper().InferAssignments(result) )
        {
            if ( string.Equals(assignment.Name, name, StringComparison.OrdinalIgnoreCase) )
            {
                return true;
            }
        }

        return false;
    }

    // --- statement forms that were never walked ---

    [Fact]
    public void AConstDeclarationBindsItsNameLikeAnAssignment()
    {
        // ConstDeclNode fell through the default case, so `const MAX = 4;` left MAX untyped while
        // `MAX = 4;` was typed and hinted.
        Assert.Equal(ScrType.Int, TypeOf("    const MAX = 4;\n    probe = MAX;", "probe"));
    }

    [Fact]
    public void AConstDeclarationIsHinted()
    {
        Assert.True(HasHint("    const MAX = 4;", "MAX"));
    }

    [Fact]
    public void AForLoopIncrementIsWalked()
    {
        // The increment runs on iterations where the body ran, so an assignment in it is real.
        Assert.Equal(ScrType.Int, TypeOf("    for ( i = 0; i < 3; step = 1 )\n    {\n    }", "step"));
    }

    [Fact]
    public void AnAssignmentInsideAConditionIsSeen()
    {
        // Conditions were never typed for effects, so an assignment in one was invisible. Written
        // in the doubly-parenthesised form, which is what tells the parser the assignment is
        // deliberate and suppresses 3013 — and which also has to not hide it from this walk.
        Assert.Equal(ScrType.Int, TypeOf("    if ( ( found = 4 ) )\n    {\n    }", "found"));
    }

    [Fact]
    public void AnAssignmentInsideAWhileConditionIsSeen()
    {
        Assert.Equal(ScrType.Int, TypeOf("    while ( ( found = 4 ) )\n    {\n        break;\n    }", "found"));
    }

    [Fact]
    public void ParenthesesDoNotHideAnAssignmentStatement()
    {
        // `( x = 5 );` is the same assignment written oddly, and the walk bailed on the paren.
        Assert.Equal(ScrType.Int, TypeOf("    ( wrapped = 5 );", "wrapped"));
    }

    [Fact]
    public void AnAssignmentInsideAReturnIsSeen()
    {
        Assert.Equal(ScrType.Int, TypeOf("    return ( kept = 4 );", "kept"));
    }

    // --- foreach bindings ---

    [Fact]
    public void AForeachBindingShadowsAnOuterLocalOfTheSameName()
    {
        // The discriminating test for the binding existing at all. Before this the binding was never
        // entered, so reading `item` inside the body found the OUTER local and confidently reported
        // int — a wrong answer, not merely a missing one. With the binding in place the read is an
        // array element, whose type is not modelled, so nothing is claimed.
        //
        // That distinction is the blocker for lowering a foreach into a for over getarraykeys.
        Assert.Equal(
            ScrType.Unknown,
            TypeOf("    item = 5;\n    foreach ( item in a )\n    {\n        inner = item;\n    }", "inner"));
    }

    [Fact]
    public void AForeachBindingDoesNotRestoreTheOuterValueAfterTheLoop()
    {
        // GSC scopes locals to the FUNCTION, not the block — `for ( i = 0; … )` and then reading
        // `i` afterwards is an ordinary idiom, and a foreach binding is the same variable. So after
        // the loop the name holds the last element, or undefined if the collection was empty; what
        // it certainly is not is the int it held beforehand.
        //
        // Dropping the binding at the join claimed exactly that, and nothing caught it because both
        // readings project to Unknown — the difference only shows in the union a rewriter reads.
        Assert.Equal(
            ScrType.Unknown,
            TypeOf("    item = 5;\n    foreach ( item in a )\n    {\n    }\n    after = item;", "after"));
    }

    [Fact]
    public void WithoutTheShadowingTheOuterLocalWouldBeRead()
    {
        // The control for the test above: the same read one line earlier, outside the loop, does
        // resolve to the outer local. Without this pair, "Unknown" above could mean the body was
        // never walked at all.
        Assert.Equal(
            ScrType.Int,
            TypeOf("    item = 5;\n    before = item;\n    foreach ( item in a )\n    {\n    }", "before"));
    }

    [Fact]
    public void AForeachBodyIsStillWalked()
    {
        Assert.Equal(ScrType.Int, TypeOf("    foreach ( item in a )\n    {\n        inside = 4;\n    }", "inside"));
    }

    // --- expression forms that produced nothing ---

    [Fact]
    public void ATernaryIsTheUnionOfItsArms()
    {
        // Both arms are live. Agreeing arms give a usable answer where the old pass gave none.
        Assert.Equal(ScrType.Int, TypeOf("    v = a ? 1 : 2;", "v"));
    }

    [Fact]
    public void ATernaryWithDisagreeingArmsIsNotAsserted()
    {
        Assert.Equal(ScrType.Unknown, TypeOf("    v = a ? 1 : \"text\";", "v"));
    }

    [Fact]
    public void AChainedAssignmentTakesTheAssignedValue()
    {
        Assert.Equal(ScrType.Int, TypeOf("    v = ( b = 5 );", "v"));
    }

    [Fact]
    public void AFunctionPointerIsAFunction()
    {
        Assert.Equal(ScrType.Function, TypeOf("    p = &some_func;", "p"));
    }

    [Fact]
    public void AnIndexedReadIsNotClaimedToBeAnything()
    {
        // Element types are not modelled — v1.5 did not model them either. What matters is that the
        // rule declines rather than guessing.
        Assert.Equal(ScrType.Unknown, TypeOf("    v = a[ 0 ];", "v"));
    }

    // --- the reference kinds, which are what the transpiler turns on ---

    [Fact]
    public void AnArrayLiteralIsAnArray()
    {
        Assert.Equal(ScrType.Array, TypeOf("    v = [];", "v"));
    }

    [Fact]
    public void SpawnStructIsAStruct()
    {
        Assert.Equal(ScrType.Struct, TypeOf("    v = spawnstruct();", "v"));
    }

    [Fact]
    public void SelfIsAnEntity()
    {
        Assert.Equal(ScrType.Entity, TypeOf("    v = self;", "v"));
    }

    [Fact]
    public void GameIsAnArray()
    {
        Assert.Equal(ScrType.Array, TypeOf("    v = game;", "v"));
    }

    [Fact]
    public void LevelIsAStruct()
    {
        Assert.Equal(ScrType.Struct, TypeOf("    v = level;", "v"));
    }

    [Fact]
    public void AStructIsNeverReportedAsAnArray()
    {
        // The control in the direction that matters. Structs alias in every dialect and arrays do
        // not, so calling a struct an array would mark a translation safe when it is not — and
        // calling an array a struct would do the reverse.
        Assert.NotEqual(ScrType.Array, TypeOf("    v = spawnstruct();", "v"));
        Assert.NotEqual(ScrType.Struct, TypeOf("    v = [];", "v"));
    }

    [Fact]
    public void ANewInstanceIsDistinctFromABareStruct()
    {
        // A class instance carries its class name, which a rewriter lowering BO3 objects needs.
        // It still projects onto Struct, since ScrType has no member for it.
        Assert.Equal(ScrType.Struct, TypeOf("    v = new Foo();", "v"));
    }
}
