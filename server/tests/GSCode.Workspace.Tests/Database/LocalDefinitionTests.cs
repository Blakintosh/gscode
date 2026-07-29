using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// Go-to-definition on a LOCAL. Locals are absent from the reference index by design — it is keyed
/// by SymbolKey and shared workspace-wide, so an `i` in one function would collide with the `i` in
/// every other — which left go-to-definition with nothing at all to find on a variable.
///
/// They are resolved from the AST instead, per function, which is the scope a local really has.
/// </summary>
public class LocalDefinitionTests
{
    private static ParseResult Analyze(string source)
    {
        return ScriptAnalysis.Analyze(
            @"C:\bo3\share\raw\scripts\main.gsc",
            ScriptLanguage.Gsc,
            SourceText.From(source),
            NullInsertProvider.Instance,
            new NameTable());
    }

    [Fact]
    public void AVariableResolvesToItsAssignment()
    {
        //                     0         1
        //                     0123456789012345
        string source = "function f()\n{\n\tcount = 1;\n\tuse( count );\n}\n";

        // The `count` inside use(), on line 3.
        TextRange? definition = LocalDefinition.Find(Analyze(source), new Position(3, 6));

        Assert.NotNull(definition);
        Assert.Equal(2, definition.Value.Start.Line);
    }

    [Fact]
    public void AParameterWinsOverALaterAssignment()
    {
        // The signature is where the name is introduced; the assignment writes to something that
        // already exists.
        string source = "function f( count )\n{\n\tcount = 1;\n\tuse( count );\n}\n";

        TextRange? definition = LocalDefinition.Find(Analyze(source), new Position(3, 6));

        Assert.NotNull(definition);
        Assert.Equal(0, definition.Value.Start.Line);
    }

    [Fact]
    public void TheLastAssignmentAtOrBeforeTheCursorWins()
    {
        // Matching what hover reports. Jumping to the FIRST would land somewhere the value no
        // longer comes from, and the two surfaces disagreeing about one variable is worse than
        // either answer on its own.
        string source = "function f()\n{\n\tx = 1;\n\tx = \"s\";\n\tuse( x );\n}\n";

        TextRange? definition = LocalDefinition.Find(Analyze(source), new Position(4, 6));

        Assert.NotNull(definition);
        Assert.Equal(3, definition.Value.Start.Line);
    }

    [Fact]
    public void AnAssignmentBelowTheCursorIsIgnored()
    {
        // It says nothing about where the value being read here came from.
        string source = "function f()\n{\n\tx = 1;\n\tuse( x );\n\tx = 2;\n}\n";

        TextRange? definition = LocalDefinition.Find(Analyze(source), new Position(3, 6));

        Assert.NotNull(definition);
        Assert.Equal(2, definition.Value.Start.Line);
    }

    [Fact]
    public void AFieldWriteDoesNotDefineABareLocal()
    {
        // `self.count` is a field on an entity that outlives the function, so it is not what a
        // bare `count` refers to.
        string source = "function f()\n{\n\tself.count = 1;\n\tuse( count );\n}\n";

        Assert.Null(LocalDefinition.Find(Analyze(source), new Position(3, 6)));
    }

    [Fact]
    public void ALocalInAnotherFunctionIsNotFound()
    {
        // The whole reason locals stay out of the shared index.
        string source = "function a()\n{\n\tcount = 1;\n}\nfunction b()\n{\n\tuse( count );\n}\n";

        Assert.Null(LocalDefinition.Find(Analyze(source), new Position(6, 6)));
    }

    [Fact]
    public void APositionOnNothingResolvesToNothing()
    {
        string source = "function f()\n{\n\tcount = 1;\n}\n";

        Assert.Null(LocalDefinition.Find(Analyze(source), new Position(1, 0)));
    }
}
