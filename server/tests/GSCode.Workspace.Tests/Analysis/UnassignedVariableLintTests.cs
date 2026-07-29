using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Analysis;
using Xunit;

namespace GSCode.Workspace.Tests.Analysis;

/// <summary>
/// Reading a name nothing ever assigns. GSC does not reject an undefined variable, so the mistake
/// surfaces at runtime as an undefined value turning up somewhere far from the typo.
///
/// Every test that asserts NOTHING is reported exists because that shape appeared in code that
/// ships and works. Measured over the corpus the rule went 2,742 reports on CoD4 alone down to 17
/// across all 7,309 scripts, and each exclusion below is one of those steps.
/// </summary>
public class UnassignedVariableLintTests
{
    private static ImmutableArray<Diagnostic> Lint(string source, string game = "bo3")
    {
        GameProfile profile = GameProfile.ByName(game)!;
        string path = game == "bo3" ? @"c:\ws\scripts\t.gsc" : @"c:\ws\maps\t.gsc";

        ParseResult result = ScriptAnalysis.Analyze(
            path, ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable(), profile);

        return UnassignedVariableLint.Analyze(result, profile);
    }

    [Theory]
    [InlineData("switch ( never_set )\n\t{\n\t}")]
    [InlineData("foo = not_defined + 5;")]
    [InlineData("if ( missing )\n\t{\n\t}")]
    [InlineData("use( absent );")]
    public void AReadOfSomethingNeverAssigned(string body)
    {
        Diagnostic diagnostic = Assert.Single(Lint("function f()\n{\n\t" + body + "\n}\n"));

        Assert.Equal(GscDiagnosticCode.VariableNeverAssigned, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Theory]
    [InlineData("function f( count )\n{\n\tuse( count );\n}\n")]                          // parameter
    [InlineData("function f()\n{\n\tx = 1;\n\tuse( x );\n}\n")]                           // assigned above
    [InlineData("function f()\n{\n\tuse( x );\n\tx = 1;\n}\n")]                           // assigned below
    [InlineData("function f( a )\n{\n\tforeach ( k, v in a )\n\t{\n\t\tuse( k );\n\t\tuse( v );\n\t}\n}\n")]
    [InlineData("function f()\n{\n\tself waittill( \"damage\", attacker, amount );\n\tuse( attacker );\n\tuse( amount );\n}\n")]
    [InlineData("function f()\n{\n\tquotes[ 0 ] = \"a\";\n\tuse( quotes );\n}\n")]        // subscript creates it
    [InlineData("function f()\n{\n\ts = SpawnStruct();\n\ts.field = 1;\n\tuse( s.field );\n}\n")]
    [InlineData("function f()\n{\n\tuse( level );\n\tuse( self );\n}\n")]                 // profile globals
    [InlineData("function f()\n{\n\thelper();\n}\n")]                                     // a call, not a read
    [InlineData("function f()\n{\n\tp = &helper;\n\tuse( p );\n}\n")]                     // function pointer
    public void ThingsThatAreNotMistakes(string source)
    {
        Assert.Empty(Lint(source));
    }

    [Fact]
    public void AFileScopeConstantIsVisibleToEveryFunction()
    {
        // The Infinity Ward dialects allow `NAME = value;` between declarations, readable from all
        // of them. Looking only inside functions reported every read of one — 755 in MW2's scripts,
        // and their ALL_CAPS naming made them look convincingly like macros.
        string source = "SPEED = 1.0;\n\nrun()\n{\n\twait 6 * SPEED;\n}\n";

        Assert.Empty(Lint(source, "mw2"));
    }

    [Fact]
    public void AnUnresolvedImportStandsTheRuleDown()
    {
        // A #define whose header never arrived survives as a plain identifier, which is
        // indistinguishable from a variable nobody assigned. When the names legally in scope are
        // unknowable, "nothing assigns this" is not a claim worth making.
        string source = "#insert scripts\\shared\\nonexistent.gsh;\n\nfunction f()\n{\n\tuse( SOME_CONSTANT );\n}\n";

        Assert.Empty(Lint(source));
    }

    [Fact]
    public void ClassMethodsAreSkipped()
    {
        // A method may reach a member without qualifying it, so the function's own writes do not
        // account for every name it can legally read.
        string source = "class C\n{\n\tvar count;\n\n\tfunction m()\n\t{\n\t\tuse( count );\n\t}\n}\n";

        Assert.Empty(Lint(source));
    }

    [Fact]
    public void AMacroSuppliedNameIsNotTheAuthorsToFix()
    {
        // The range would point into an expansion rather than at anything they wrote.
        string source = "#define USE_IT use( hidden_name );\n\nfunction f()\n{\n\tUSE_IT\n}\n";

        Assert.Empty(Lint(source));
    }
}
