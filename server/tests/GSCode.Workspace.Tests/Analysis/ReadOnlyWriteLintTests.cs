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
/// Fixtures chosen from the real bundled field data: accuratefire is read-only on every kind
/// that declares it, accuracy is writable, and radius is one of only two names that are
/// read-only on some kinds and writable on others — the case the lint must stay silent about.
/// </summary>
public class ReadOnlyWriteLintTests
{
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static ImmutableArray<Diagnostic> Lint(string body)
    {
        string source = "function run()\n{\n    " + body + "\n}\n";
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        return ReadOnlyWriteLint.Analyze(result, ObjectFields.Load(ApiDirectory));
    }

    [Fact]
    public void AssigningToSize_IsAnError()
    {
        Diagnostic diagnostic = Assert.Single(Lint("self.size = 5;"));

        Assert.Equal(GscDiagnosticCode.SizeIsReadOnly, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void IncrementingSize_IsAnError()
    {
        // ++ reads and writes in one step, so it is still a write.
        Assert.Equal(GscDiagnosticCode.SizeIsReadOnly, Assert.Single(Lint("self.size++;")).Code);
    }

    [Fact]
    public void ReadingSize_IsFine()
    {
        Assert.Empty(Lint("count = self.size;"));
    }

    [Fact]
    public void AssigningToAReadOnlyEngineField_Warns()
    {
        Diagnostic diagnostic = Assert.Single(Lint("self.accuratefire = 1;"));

        Assert.Equal(GscDiagnosticCode.ReadOnlyFieldWrite, diagnostic.Code);
        // A warning, not an error: the flag comes from curated data that can carry mistakes.
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("accuratefire", diagnostic.Message);
    }

    [Fact]
    public void CompoundAssignmentToAReadOnlyField_Warns()
    {
        Assert.Equal(GscDiagnosticCode.ReadOnlyFieldWrite, Assert.Single(Lint("self.accuratefire += 1;")).Code);
    }

    [Fact]
    public void AssigningToAWritableEngineField_IsFine()
    {
        Assert.Empty(Lint("self.accuracy = 1;"));
    }

    [Fact]
    public void FieldReadOnlyOnSomeKindsButNotOthers_IsNotFlagged()
    {
        // radius is read-only on some entity kinds and writable on others. The owner's kind
        // isn't inferred here, so flagging it would be a guess.
        Assert.Empty(Lint("self.radius = 32;"));
    }

    [Fact]
    public void UnknownFieldName_IsFine()
    {
        Assert.Empty(Lint("self.my_own_field = 1;"));
    }

    [Fact]
    public void ReadingAReadOnlyField_IsFine()
    {
        Assert.Empty(Lint("x = self.accuratefire;"));
    }
}
