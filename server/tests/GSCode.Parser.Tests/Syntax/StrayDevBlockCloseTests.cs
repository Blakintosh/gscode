using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Syntax;

/// <summary>
/// A `#/` with nothing open is tolerated rather than reported. Stock scripts ship that way —
/// vehicle_shared.gsc has 13 closes to 12 opens — and the engine compiles them, so erroring
/// would flag code a user cannot act on. The marker is SKIPPED, never treated as a delimiter,
/// so it can never change which code is considered dev-only.
/// </summary>
public class StrayDevBlockCloseTests
{
    private static ParseResult Analyze(string source)
    {
        return ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());
    }

    private static ImmutableArray<Diagnostic> Errors(ParseResult result)
    {
        return result.AllDiagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();
    }

    [Fact]
    public void StrayCloseAtTopLevel_IsIgnored()
    {
        // The vehicle_shared.gsc shape: a trailing orphan after everything else.
        ParseResult result = Analyze("function f()\n{\n}\n\n#/\n");

        Assert.Empty(Errors(result));
        Assert.Single(result.Extraction.Functions);
    }

    [Fact]
    public void StrayCloseBetweenDeclarations_DoesNotSwallowWhatFollows()
    {
        // The declaration after the stray marker must still be extracted.
        ParseResult result = Analyze("function before()\n{\n}\n#/\nfunction after()\n{\n}\n");

        Assert.Empty(Errors(result));
        Assert.Equal(2, result.Extraction.Functions.Length);
    }

    [Fact]
    public void StrayCloseInsideAFunction_IsIgnored()
    {
        ParseResult result = Analyze("function f()\n{\n    x = 1;\n    #/\n    y = 2;\n}\n");

        Assert.Empty(Errors(result));
        Assert.Single(result.Extraction.Functions);
    }

    [Fact]
    public void BalancedDevBlock_StillParsesAsADevBlock()
    {
        // The tolerance must not disturb the normal case: a real block still nests.
        ParseResult result = Analyze("/#\nfunction dev_only()\n{\n}\n#/\n");

        Assert.Empty(Errors(result));
        Assert.Single(result.Extraction.Functions);
    }

    [Fact]
    public void BalancedStatementDevBlock_StillParses()
    {
        ParseResult result = Analyze("function f()\n{\n    /#\n    debug_print();\n    #/\n}\n");

        Assert.Empty(Errors(result));
    }

    [Fact]
    public void UnterminatedOpen_IsStillReported()
    {
        // The opposite imbalance is a real mistake and keeps its diagnostic — tolerating a
        // stray close must not silently tolerate a missing one.
        ParseResult result = Analyze("/#\nfunction dev_only()\n{\n}\n");

        Assert.Contains(Errors(result), diagnostic => diagnostic.Code == GscDiagnosticCode.UnterminatedDevBlock);
    }
}
