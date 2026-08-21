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
/// The v1 rule's canonical bug: a flag-subset type test matched int parameters (Int carries
/// the Bool bit), so legitimate literal 0/1 arguments were flagged. These re-express that
/// scenario against the real bundled API — AllowAttack takes a bool, ActivateClientExploder
/// takes an int.
/// </summary>
public class PreferBooleanLiteralLintTests
{
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static ImmutableArray<Diagnostic> Lint(string body)
    {
        string source = "function run()\n{\n    " + body + "\n}\n";
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        BuiltinApiSet builtins = BuiltinApiSet.Load(ApiDirectory);
        BuiltinApi api = builtins.For(ScriptLanguage.Gsc);

        // Synthetic field data, for the same reason ReadOnlyWriteLintTests uses it: this pins the
        // RULE, and must keep doing so however the bundled types move.
        ObjectFields fields = ObjectFields.Create(
            [
                new("dogibbing", "bool", ReadOnly: false, "ai"),
                new("accuracy", "float", ReadOnly: false, "ai"),
                // bool on one entity kind, not on another: not evidence.
                new("mixed", "bool", ReadOnly: false, "ai"),
                new("mixed", "int", ReadOnly: false, "player"),
                // Declared bool on weapon alone, which is not an entity.
                new("isemp", "bool", ReadOnly: true, "weapon"),
            ],
            []);

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(NodeLintHarness.Run(
            result,
            (node, into) => PreferBooleanLiteralLint.InspectNode(node, api, into)));

        // The second half, which the server calls once per file outside the shared walk.
        PreferBooleanLiteralLint.InspectRest(result, fields, new FlowTyper(api, fields), diagnostics);
        return diagnostics.ToImmutable();
    }

    [Theory]
    [InlineData("AllowAttack( 0 );")]
    [InlineData("AllowAttack( 1 );")]
    public void BoolParameter_LiteralZeroOrOne_Hints(string call)
    {
        Diagnostic diagnostic = Assert.Single(Lint(call));

        Assert.Equal(GscDiagnosticCode.PreferBooleanLiteral, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Hint, diagnostic.Severity);
    }

    [Fact]
    public void Hint_NamesTheReplacementLiteral()
    {
        Assert.Contains("true", Assert.Single(Lint("AllowAttack( 1 );")).Message);
        Assert.Contains("false", Assert.Single(Lint("AllowAttack( 0 );")).Message);
    }

    [Theory]
    [InlineData("ActivateClientExploder( 0 );")]
    [InlineData("ActivateClientExploder( 1 );")]
    public void IntParameter_LiteralZeroOrOne_DoesNotHint(string call)
    {
        // The whole point of the rule's scope: an int parameter legitimately takes 0 and 1.
        Assert.Empty(Lint(call));
    }

    [Theory]
    [InlineData("AllowAttack( true );")]
    [InlineData("AllowAttack( false );")]
    public void BoolParameter_BooleanKeyword_DoesNotHint(string call)
    {
        Assert.Empty(Lint(call));
    }

    [Fact]
    public void OtherIntegers_AreNotFlagged()
    {
        // Only 0 and 1 are plausible boolean stand-ins.
        Assert.Empty(Lint("AllowAttack( 7 );"));
    }

    [Fact]
    public void UnknownFunction_IsNotFlagged()
    {
        Assert.Empty(Lint("my_own_helper( 1 );"));
    }

    [Fact]
    public void NestedCall_IsStillInspected()
    {
        // The walk must reach calls inside statements, not just top-level expressions.
        Assert.Single(Lint("if ( isdefined( self ) )\n    {\n        AllowAttack( 1 );\n    }"));
    }

    // --- Engine fields declared bool ---

    [Theory]
    [InlineData("self.dogibbing = 1;", "true")]
    [InlineData("self.dogibbing = 0;", "false")]
    public void ABoolFieldAssignedAnIntLiteral_Hints(string body, string replacement)
    {
        // GSC has no bool, so the int is legal -- hence a Hint, not a warning. The field data is
        // what knows this field is a flag.
        Diagnostic hint = Assert.Single(Lint(body));

        Assert.Equal(GscDiagnosticCode.PreferBooleanLiteral, hint.Code);
        Assert.Equal(DiagnosticSeverity.Hint, hint.Severity);
        Assert.Contains(replacement, hint.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("self.dogibbing = true;")]
    [InlineData("self.dogibbing = false;")]
    [InlineData("self.dogibbing = other;")]
    [InlineData("self.dogibbing = 2;")]
    public void ABoolFieldAssignedAnythingElse_IsFine(string body)
    {
        // Only 0 and 1 have a boolean literal to suggest; 2 is a real int the author meant.
        Assert.Empty(Lint(body));
    }

    [Fact]
    public void ANonBoolFieldIsNotHinted()
    {
        Assert.Empty(Lint("self.accuracy = 1;"));
    }

    [Fact]
    public void AFieldBoolOnOnlySomeEntityKinds_IsNotHinted()
    {
        // The owner's exact kind is not inferred, so a disagreement between kinds proves nothing.
        Assert.Empty(Lint("self.mixed = 1;"));
    }

    [Fact]
    public void AFieldBoolOnWeaponAlone_IsNotHintedOnAnEntity()
    {
        // A weapon is what GetWeapon() returns, not an entity -- the same scoping ReadOnlyWriteLint
        // applies, for the same reason.
        Assert.Empty(Lint("self.isemp = 1;"));
    }

    [Fact]
    public void AStructFieldIsNotHinted()
    {
        // The engine's flag says nothing about a struct you made.
        Assert.Empty(Lint("bag = SpawnStruct();\n    bag.dogibbing = 1;"));
    }

    [Fact]
    public void ACompoundWriteIsNotHinted()
    {
        // `+=` has no single assigned value to replace with true or false.
        Assert.Empty(Lint("self.dogibbing += 1;"));
    }

    [Fact]
    public void AnUntypedOwnerIsNotHinted()
    {
        Assert.Empty(Lint("thing = mystery();\n    thing.dogibbing = 1;"));
    }
}
