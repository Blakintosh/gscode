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

public class UnusedBindingLintTests
{
    private static ImmutableArray<Diagnostic> Lint(string source)
    {
        ParseResult result = ScriptAnalysis.Analyze(
            TestPaths.Raw(@"scripts\t.gsc"), ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        return UnusedBindingLint.Analyze(result);
    }

    [Fact]
    public void AWaittillOutputNeverReadIsFaded()
    {
        string source = "function f()\n{\n\tself waittill( \"damage\", attacker );\n}\n";

        Diagnostic diagnostic = Assert.Single(Lint(source));
        Assert.Equal(GscDiagnosticCode.UnusedBinding, diagnostic.Code);
    }

    [Fact]
    public void AWaittillMatchArgument_IsAReadNotAnOutput_SoItIsNotFaded()
    {
        // The reported false positive: `waittillmatch`'s trailing argument is the value to MATCH
        // against the notify's own parameters — a read, unlike `waittill`, which BINDS its trailing
        // arguments as outputs the engine fills in. Treating the two identically faded a parameter
        // that is genuinely used, and separately reported it as an unused "waittill output" it never
        // was.
        string source = "function f( matchname )\n{\n\tself waittillmatch( \"single anim\", matchname );\n}\n";

        Assert.Empty(Lint(source));
    }
}
