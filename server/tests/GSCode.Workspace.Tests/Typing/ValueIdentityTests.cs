using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Parser.Syntax.Ast;
using GSCode.Workspace.Api;
using GSCode.Workspace.Typing;
using Xunit;

namespace GSCode.Workspace.Tests.Typing;

/// <summary>
/// Two facts the coarse <see cref="ScrType"/> projection cannot hold — which class an instance is,
/// and which function a pointer holds — and which the editor surfaces need.
///
/// Both were computed and then discarded at the boundary: <c>new derived_thing()</c> was shown as
/// <c>struct</c> and every <c>[[ ptr ]]( … )</c> call showed no parameter names at all, because the
/// only thing carried out of the walk was the projection.
/// </summary>
public class ValueIdentityTests
{
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static FlowTyper NewTyper()
    {
        return new FlowTyper(ApiLoader.Load(ApiDirectory, ScriptLanguage.Gsc), ObjectFields.Load(ApiDirectory));
    }

    private static ParseResult Analyze(string source)
    {
        return ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());
    }

    private static InferredAssignment AssignmentTo(string source, string name)
    {
        ParseResult result = Analyze(source);

        foreach ( InferredAssignment assignment in NewTyper().InferAssignments(result) )
        {
            if ( string.Equals(assignment.Name, name, StringComparison.Ordinal) )
            {
                return assignment;
            }
        }

        Assert.Fail($"nothing inferred for '{name}'");
        return default;
    }

    // --- The label a reader sees ---

    [Fact]
    public void AClassInstanceIsShownAsItsClass()
    {
        InferredAssignment thing = AssignmentTo(
            "class widget\n{\n}\n\nfunction f()\n{\n    thing = new widget();\n}\n", "thing");

        Assert.Equal("widget", thing.Display);

        // The projection is unchanged, so the typing lints judge it exactly as before.
        Assert.Equal(ScrType.Struct, thing.Type);
    }

    [Fact]
    public void AHashLiteralIsShownAsHash()
    {
        InferredAssignment hashed = AssignmentTo("function f()\n{\n    h = #\"some_string\";\n}\n", "h");

        Assert.Equal("hash", hashed.Display);
        Assert.Equal(ScrType.Int, hashed.Type);
    }

    [Fact]
    public void EveryOtherTypeKeepsTheNameItAlreadyHad()
    {
        // The guard on the change: only the two lossy cases move, so no other label shifts.
        Assert.Equal("int", AssignmentTo("function f()\n{\n    a = 1;\n}\n", "a").Display);
        Assert.Equal("string", AssignmentTo("function f()\n{\n    a = \"x\";\n}\n", "a").Display);
        Assert.Equal("float", AssignmentTo("function f()\n{\n    a = 1.5;\n}\n", "a").Display);
        Assert.Equal("array", AssignmentTo("function f()\n{\n    a = [];\n}\n", "a").Display);
        Assert.Equal("vector", AssignmentTo("function f()\n{\n    a = ( 0, 0, 1 );\n}\n", "a").Display);
        Assert.Equal("function", AssignmentTo("function f()\n{\n    a = &f;\n}\n", "a").Display);
    }

    [Fact]
    public void AnInstanceWithNoKnownClassStillReadsAsStruct()
    {
        // SpawnStruct() is a struct and names no class, so the fallback is what shows.
        Assert.Equal("struct", AssignmentTo("function f()\n{\n    s = spawnstruct();\n}\n", "s").Display);
    }

    // --- What a pointer points at ---

    [Fact]
    public void AnAddressOfCarriesTheFunctionItNames()
    {
        InferredAssignment pointer = AssignmentTo("function f()\n{\n    p = &helper;\n}\n", "p");

        ScrFunctionRef? reference = pointer.Value.FunctionTarget;
        Assert.NotNull(reference);
        Assert.Null(reference.Value.Namespace);
        Assert.Equal("helper", reference.Value.Name);
    }

    [Fact]
    public void ANamespacedAddressOfCarriesBothParts()
    {
        InferredAssignment pointer = AssignmentTo("function f()\n{\n    p = &util::helper;\n}\n", "p");

        ScrFunctionRef? reference = pointer.Value.FunctionTarget;
        Assert.NotNull(reference);
        Assert.Equal("util", reference.Value.Namespace);
        Assert.Equal("helper", reference.Value.Name);
    }

    [Fact]
    public void TwoBranchesAssigningDifferentPointersLeaveTheTargetUnknown()
    {
        // The same rule the class name follows: a label naming one of two would be wrong half the
        // time it is shown.
        ParseResult result = Analyze(
            "function f( c )\n{\n    if ( c )\n    {\n        p = &one;\n    }\n    else\n    {\n        p = &two;\n    }\n\n    q = p;\n}\n");

        FlowTyper typer = NewTyper();
        foreach ( InferredAssignment assignment in typer.InferAssignments(result) )
        {
            if ( string.Equals(assignment.Name, "q", StringComparison.Ordinal) )
            {
                Assert.Null(assignment.Value.FunctionTarget);
                return;
            }
        }

        Assert.Fail("nothing inferred for 'q'");
    }

    [Fact]
    public void ADereferenceReportsTheFunctionThePointerHolds()
    {
        // What the inlay-hint pass asks: given the `[[ p ]]` node, whose parameters am I naming?
        ParseResult result = Analyze("function f()\n{\n    p = &helper;\n    r = [[ p ]]( 1 );\n}\n");
        ScriptTypes types = NewTyper().InferValues(result);

        PointerDerefNode? deref = null;
        foreach ( KeyValuePair<ExprNode, ScrValue> entry in types.All )
        {
            if ( entry.Key is PointerDerefNode found )
            {
                deref = found;
            }
        }

        Assert.NotNull(deref);

        ScrFunctionRef? reference = types.ValueOf(deref).FunctionTarget;
        Assert.NotNull(reference);
        Assert.Equal("helper", reference.Value.Name);
    }

    [Fact]
    public void AMethodCallObjectIsTypedSoItsClassIsKnown()
    {
        // `[[ thing ]]->bump()` — the class comes from the object, and the walk had never typed it.
        ParseResult result = Analyze(
            "class widget\n{\n    function bump( amount )\n    {\n    }\n}\n\nfunction f()\n{\n    thing = new widget();\n    [[ thing ]]->bump( 1 );\n}\n");

        ScriptTypes types = NewTyper().InferValues(result);

        foreach ( KeyValuePair<ExprNode, ScrValue> entry in types.All )
        {
            if ( entry.Key is IdentifierNode { Token.Text: "thing" } )
            {
                Assert.Equal("widget", entry.Value.InstanceClass);
                return;
            }
        }

        Assert.Fail("the method call's object was never typed");
    }
}
