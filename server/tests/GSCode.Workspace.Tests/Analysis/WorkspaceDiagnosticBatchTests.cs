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
/// 5018 duplicate import, 5019 a void result kept, and 5020 a bound name nothing uses.
/// </summary>
public class WorkspaceDiagnosticBatchTests
{
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static ParseResult Analyze(string source)
    {
        return ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc",
            ScriptLanguage.Gsc,
            SourceText.From(source),
            NullInsertProvider.Instance,
            new NameTable());
    }

    // --- 5018: the same file imported twice ---

    [Fact]
    public void AFileImportedTwice()
    {
        Diagnostic duplicate = Assert.Single(DuplicateImportLint.Analyze(
            Analyze("#using scripts\\shared\\util;\n#using scripts\\shared\\util;\n")));

        Assert.Equal(GscDiagnosticCode.DuplicateImport, duplicate.Code);

        // Tagged as well as reported: the whole line can go, and greying it out says so directly.
        Assert.Contains(DiagnosticTag.Unnecessary, duplicate.Tags);
    }

    [Fact]
    public void SeparatorsAndCaseDoNotMakeItADifferentFile()
    {
        // The engine resolves either spelling, so these import the same file.
        Assert.Single(DuplicateImportLint.Analyze(
            Analyze("#using scripts/shared/util;\n#using scripts\\shared\\Util;\n")));
    }

    [Fact]
    public void DistinctImportsAreFine()
    {
        Assert.Empty(DuplicateImportLint.Analyze(
            Analyze("#using scripts\\shared\\util;\n#using scripts\\shared\\array;\n")));
    }

    /// <summary>
    /// The rule as the server runs it: the shared per-node walk, gated on `Applies`, which stands
    /// the rule down on a game that bundles no builtin library.
    /// </summary>
    private static ImmutableArray<Diagnostic> RunVoidResultLint(ParseResult result, BuiltinApi builtins)
    {
        if ( !VoidResultLint.Applies(builtins) )
        {
            return [];
        }

        return NodeLintHarness.Run(
            result,
            (node, diagnostics) => VoidResultLint.InspectNode(node, builtins, diagnostics));
    }

    // --- 5019: keeping the result of something that returns nothing ---

    [Fact]
    public void AssigningTheResultOfAVoidBuiltin()
    {
        BuiltinApi builtins = ApiLoader.Load(ApiDirectory, ScriptLanguage.Gsc);

        Diagnostic diagnostic = Assert.Single(RunVoidResultLint(
            Analyze("function f()\n{\n\tx = PrintLn( \"a\" );\n}\n"), builtins));

        Assert.Equal(GscDiagnosticCode.VoidResultAssigned, diagnostic.Code);
    }

    [Fact]
    public void CallingItWithoutKeepingTheResultIsFine()
    {
        // The mistake is only visible where the value is KEPT; the call itself is ordinary.
        BuiltinApi builtins = ApiLoader.Load(ApiDirectory, ScriptLanguage.Gsc);

        Assert.Empty(RunVoidResultLint(
            Analyze("function f()\n{\n\tPrintLn( \"a\" );\n}\n"), builtins));
    }

    [Fact]
    public void ABuiltinThatReturnsSomethingIsFine()
    {
        BuiltinApi builtins = ApiLoader.Load(ApiDirectory, ScriptLanguage.Gsc);

        Assert.Empty(RunVoidResultLint(
            Analyze("function f()\n{\n\tt = GetTime();\n}\n"), builtins));
    }

    [Fact]
    public void AScriptFunctionIsNotJudged()
    {
        // GSC declares no return type, and a function that returns on some paths and falls off the
        // end on others is legal, so the same claim about a script function would be a guess.
        BuiltinApi builtins = ApiLoader.Load(ApiDirectory, ScriptLanguage.Gsc);

        Assert.Empty(RunVoidResultLint(
            Analyze("function f()\n{\n\tx = helper();\n}\nfunction helper()\n{\n}\n"), builtins));
    }

    // --- 5020: a bound name nothing uses ---

    [Fact]
    public void AnUnusedParameterIsFadedNotReported()
    {
        Diagnostic diagnostic = Assert.Single(UnusedBindingLint.Analyze(
            Analyze("function f( unused )\n{\n\tx = 1;\n}\n")));

        Assert.Equal(GscDiagnosticCode.UnusedBinding, diagnostic.Code);

        // Hint, so it never reaches the Problems panel — the fade is the whole output. At any
        // panel-visible severity this rule would report 5,277 findings on BO3's shipped scripts.
        Assert.Equal(DiagnosticSeverity.Hint, diagnostic.Severity);
        Assert.Contains(DiagnosticTag.Unnecessary, diagnostic.Tags);
    }

    [Fact]
    public void AnUnusedWaittillOutput()
    {
        // The author's own choice, unlike a callback parameter, so this one really can be deleted:
        // `waittill( "damage" )` is the fix.
        Diagnostic diagnostic = Assert.Single(UnusedBindingLint.Analyze(
            Analyze("function f()\n{\n\tself waittill( \"damage\", attacker );\n}\n")));

        Assert.Equal(GscDiagnosticCode.UnusedBinding, diagnostic.Code);
        Assert.Contains("attacker", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWaittillOutputThatIsUsedIsFine()
    {
        Assert.Empty(UnusedBindingLint.Analyze(
            Analyze("function f()\n{\n\tself waittill( \"damage\", attacker );\n\tuse( attacker );\n}\n")));
    }

    [Fact]
    public void ABindingIsNotAUseOfItself()
    {
        // The reason mentions and bindings are separated in one walk: counting `attacker` at the
        // waittill as a use would mean no output was ever unused.
        Assert.Single(UnusedBindingLint.Analyze(
            Analyze("function f()\n{\n\tself waittill( \"damage\", attacker );\n}\n")));
    }

    [Fact]
    public void AParameterAssignedToCountsAsUsed()
    {
        // `out = 1` does something in GSC when the argument is by-reference, and telling that apart
        // needs knowledge this rule does not have — so a mention is a mention.
        Assert.Empty(UnusedBindingLint.Analyze(
            Analyze("function f( out )\n{\n\tout = 1;\n}\n")));
    }

    [Fact]
    public void AVarargsFunctionsParametersAreNotJudged()
    {
        // It reaches its arguments through the vararg mechanism as well as by name.
        Assert.Empty(UnusedBindingLint.Analyze(
            Analyze("function f( a, ... )\n{\n\tx = 1;\n}\n")));
    }
}
