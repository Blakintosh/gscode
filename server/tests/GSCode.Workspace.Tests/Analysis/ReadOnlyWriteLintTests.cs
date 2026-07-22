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
/// The lint reports only when the OWNER's type makes a field read-only — never on the field name
/// alone. Names collide across worlds: `name` is read-only on the engine's player and weapon but
/// is an ordinary field on a struct you made, and `.size` is the implicit member of arrays and
/// strings but a normal field name on anything else.
///
/// Field data is synthetic rather than the bundled artifacts, because this tests the RULE and the
/// rule must stay pinned however the data moves. It moved: the bundled read-only flags turned out
/// to have no source and were removed, so no field carries one today and fixtures drawn from real
/// data would silently stop exercising the read-only path at all rather than fail.
///
/// The fixtures name the three cases that matter: `accuratefire` read-only wherever it is declared,
/// `accuracy` writable, and `radius` read-only on one kind and writable on another.
/// </summary>
public class ReadOnlyWriteLintTests
{
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static readonly ObjectField[] s_fields =
    [
        new("accuratefire", "int", ReadOnly: true, "ai"),
        new("accuratefire", "int", ReadOnly: true, "weapon"),
        new("accuracy", "float", ReadOnly: false, "ai"),
        new("radius", "float", ReadOnly: true, "ai"),
        new("radius", "float", ReadOnly: false, "trigger"),
        // Read-only on the engine's own kinds, an ordinary field on a struct you made: the
        // collision the owner-type check exists for.
        new("name", "string", ReadOnly: true, "player"),
        new("name", "string", ReadOnly: true, "weapon"),
    ];

    private static ImmutableArray<Diagnostic> Lint(string body)
    {
        string source = "function run()\n{\n    " + body + "\n}\n";
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        ObjectFields fields = ObjectFields.Create(s_fields, []);
        FlowTyper typer = new(ApiLoader.Load(ApiDirectory, ScriptLanguage.Gsc), fields);

        return ReadOnlyWriteLint.Analyze(result, fields, typer);
    }

    // --- The reported bug ---

    [Fact]
    public void WritingAFieldOnAStruct_IsFine()
    {
        // The reported shape. `name` is read-only on player and weapon, but this owner is a
        // struct, so the engine's flag says nothing about it.
        Assert.Empty(Lint("state_machine = SpawnStruct();\n    state_machine.name = \"idle\";"));
    }

    [Fact]
    public void WritingAReadOnlyFieldOnAnEntity_StillWarns()
    {
        // The fix must not silence the true positive it exists for.
        Diagnostic diagnostic = Assert.Single(Lint("self.accuratefire = 1;"));

        Assert.Equal(GscDiagnosticCode.ReadOnlyFieldWrite, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("accuratefire", diagnostic.Message);
    }

    [Fact]
    public void CompoundWriteToAReadOnlyEntityField_StillWarns()
    {
        Assert.Equal(GscDiagnosticCode.ReadOnlyFieldWrite, Assert.Single(Lint("self.accuratefire += 1;")).Code);
    }

    [Fact]
    public void WritingAReadOnlyNameOnAnUntypedOwner_IsFine()
    {
        // The owner cannot be typed, so the field's read-only flag proves nothing. Silence beats
        // a false error — this is the whole class the struct bug belonged to.
        Assert.Empty(Lint("thing = mystery_function();\n    thing.accuratefire = 1;"));
    }

    // --- Engine field rules that still hold ---

    [Fact]
    public void WritingAWritableEngineField_IsFine()
    {
        Assert.Empty(Lint("self.accuracy = 1;"));
    }

    [Fact]
    public void FieldReadOnlyOnSomeKindsButNotOthers_IsNotFlagged()
    {
        // radius is read-only on some entity kinds and writable on others; the owner's exact
        // kind is not inferred, so flagging it would be a guess.
        Assert.Empty(Lint("self.radius = 32;"));
    }

    [Fact]
    public void ReadingAReadOnlyField_IsFine()
    {
        Assert.Empty(Lint("x = self.accuratefire;"));
    }

    [Fact]
    public void UnknownFieldName_IsFine()
    {
        Assert.Empty(Lint("self.my_own_field = 1;"));
    }

    // --- .size ---

    [Fact]
    public void AssigningToSizeOnAnArray_IsAnError()
    {
        Diagnostic diagnostic = Assert.Single(Lint("items = [];\n    items.size = 5;"));

        Assert.Equal(GscDiagnosticCode.SizeIsReadOnly, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void IncrementingSizeOnAnArray_IsAnError()
    {
        // ++ reads and writes in one step, so it is still a write.
        Assert.Equal(GscDiagnosticCode.SizeIsReadOnly, Assert.Single(Lint("items = [];\n    items.size++;")).Code);
    }

    [Fact]
    public void AssigningToSizeOnAStruct_IsFine()
    {
        // `.size` is the implicit member of arrays and strings only. On a struct you populated
        // yourself, `size` is an ordinary field name.
        Assert.Empty(Lint("bag = SpawnStruct();\n    bag.size = 5;"));
    }

    [Fact]
    public void AssigningToSizeOnAnEntity_IsFine()
    {
        // Previously reported, which was wrong: an entity is not an array, so `self.size` is
        // just a field. This test documents the corrected semantics.
        Assert.Empty(Lint("self.size = 5;"));
    }

    [Fact]
    public void ReadingSize_IsFine()
    {
        Assert.Empty(Lint("items = [];\n    count = items.size;"));
    }
}
