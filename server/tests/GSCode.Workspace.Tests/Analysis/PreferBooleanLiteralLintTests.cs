using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Analysis;
using GSCode.Workspace.Api;
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
        return PreferBooleanLiteralLint.Analyze(result, builtins.For(ScriptLanguage.Gsc));
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
}
