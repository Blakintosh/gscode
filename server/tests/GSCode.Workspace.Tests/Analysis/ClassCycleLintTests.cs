using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Parser;
using GSCode.Workspace.Analysis;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using Xunit;

namespace GSCode.Workspace.Tests.Analysis;

public class ClassCycleLintTests
{
    /// <summary>
    /// Lints <paramref name="askingSource"/> as scripts\main.gsc, with <paramref name="otherSource"/>
    /// indexed as scripts\other.gsc so a chain can cross a file boundary — which is the shape 4 of
    /// the stock BO3 classes actually have.
    ///
    /// The asking file is indexed too. Past the first link the walk resolves parents through the
    /// STORE, so a cycle whose second class lives in the file being linted is invisible until that
    /// file has been indexed — only the depth-1 <c>class A : A</c> case is answered from the parse
    /// alone. Indexing it here keeps these tests measuring the rule rather than that lag.
    /// </summary>
    private static ImmutableArray<Diagnostic> Lint(string askingSource, string otherSource = "")
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(TestPaths.Raw(@"scripts\main.gsc"), askingSource)
            .AddFile(TestPaths.Raw(@"scripts\other.gsc"), otherSource);

        using TestWorkspace workspace = TestWorkspace.Build(files, mode: IndexingMode.Partial);
        ScriptDatabase database = workspace.Database;

        string askingPath = TestPaths.Raw(@"scripts\main.gsc");
        ParseResult result = TestParse.Analyze(askingSource, askingPath);

        return ClassCycleLint.Analyze(result, database.Gsc, "raw");
    }

    [Fact]
    public void SelfInheritance_IsReported()
    {
        ImmutableArray<Diagnostic> diagnostics = Lint("class A : A\n{\n}\n");

        Assert.Single(diagnostics);
        Assert.Equal(GscDiagnosticCode.ClassInheritanceCycle, diagnostics[0].Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostics[0].Severity);
    }

    [Fact]
    public void TwoClassCycle_IsReportedAtBothEnds()
    {
        // Each end is a class declared in this file, and the rule reports per declaration, so a
        // cycle wholly inside one file yields one diagnostic per participant.
        ImmutableArray<Diagnostic> diagnostics = Lint("class A : B\n{\n}\nclass B : A\n{\n}\n");

        Assert.Equal(2, diagnostics.Length);
    }

    [Fact]
    public void TheMessage_NamesTheFullChainInTheAuthorsOwnCasing()
    {
        // The chain used to be built from KeyName (lowercase-canonical) and dropped both endpoints,
        // so "class Foo inherits from itself" reported only "through bar" — neither the case the
        // author wrote nor either class the reader actually needs to see the loop close.
        ImmutableArray<Diagnostic> diagnostics = Lint("class Foo : Bar\n{\n}\nclass Bar : Foo\n{\n}\n");

        Assert.Contains("Foo -> Bar -> Foo", diagnostics[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDirectSelfInheritanceMessage_NamesTheClassOnBothEnds()
    {
        Diagnostic diagnostic = Assert.Single(Lint("class Foo : Foo\n{\n}\n"));

        Assert.Contains("Foo -> Foo", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CycleThroughAnotherFile_IsReported()
    {
        ImmutableArray<Diagnostic> diagnostics = Lint(
            "class A : B\n{\n}\n",
            "class B : A\n{\n}\n");

        Assert.Single(diagnostics);
        Assert.Equal(GscDiagnosticCode.ClassInheritanceCycle, diagnostics[0].Code);
    }

    [Fact]
    public void PlainInheritanceChain_IsNotReported()
    {
        ImmutableArray<Diagnostic> diagnostics = Lint(
            "class C : B\n{\n}\n",
            "class B : A\n{\n}\nclass A\n{\n}\n");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void UnresolvedParent_IsNotReported()
    {
        // "cannot find the parent" is a different fact with a different owner; the cycle rule must
        // not turn a missing class into a false inheritance loop.
        ImmutableArray<Diagnostic> diagnostics = Lint("class A : Missing\n{\n}\n");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void CycleNotPassingThroughTheDeclaredClass_IsLeftToItsOwnParticipants()
    {
        // A : B with B : C and C : B. The loop is real but A is not in it, and B and C are where it
        // gets reported, so linting A must stay silent rather than blame it for a chain it only
        // points into.
        ImmutableArray<Diagnostic> diagnostics = Lint(
            "class A : B\n{\n}\n",
            "class B : C\n{\n}\nclass C : B\n{\n}\n");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ClassWithNoParent_IsNotReported()
    {
        ImmutableArray<Diagnostic> diagnostics = Lint("class A\n{\n}\n");

        Assert.Empty(diagnostics);
    }
}
