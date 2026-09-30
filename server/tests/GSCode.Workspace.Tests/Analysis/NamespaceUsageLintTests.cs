using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Workspace.Analysis;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Analysis;

public class NamespaceUsageLintTests
{
    private static readonly GameProfile s_cod4 = GameProfile.ByName("cod4")!;

    private static (ScriptDatabase Database, PathResolver Resolver) BuildWorkspace()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(TestPaths.Raw(@"scripts\util.gsc"), "#namespace util;\nfunction helper()\n{\n}\n");

        using TestWorkspace workspace = TestWorkspace.Build(files, mode: IndexingMode.Partial);
        ScriptDatabase database = workspace.Database;
        PathResolver resolver = workspace.Resolver;

        return (database, resolver);
    }

    private static ImmutableArray<Diagnostic> Lint(string askingSource)
    {
        (ScriptDatabase database, PathResolver resolver) = BuildWorkspace();
        string askingPath = TestPaths.Raw(@"scripts\main.gsc");
        ParseResult result = TestParse.Analyze(askingSource, askingPath);

        return NamespaceUsageLint.Analyze(result, database.Gsc, ScriptLanguage.Gsc, resolver, askingPath);
    }

    [Fact]
    public void Reports_WhenQualifiedCallNamespaceIsNotImported()
    {
        string source = "#namespace game;\nfunction run()\n{\n    util::helper();\n}\n";

        ImmutableArray<Diagnostic> diagnostics = Lint(source);

        Assert.Single(diagnostics);
        Assert.Equal(GscDiagnosticCode.NamespaceNotImported, diagnostics[0].Code);
    }

    [Fact]
    public void TheReport_IsAnError()
    {
        // The script does not LINK without the import, so this is a broken build rather than a
        // matter of style. It ran as a Warning first on purpose — the rule had only just stopped
        // misfiring on class-method calls — and was promoted after holding at zero across the stock
        // corpus. Pinned because a severity is the part of a lint users actually feel, and nothing
        // asserted it when it changed.
        string source = "#namespace game;\nfunction run()\n{\n    util::helper();\n}\n";

        Assert.Equal(DiagnosticSeverity.Error, Lint(source)[0].Severity);
    }

    [Fact]
    public void NoDiagnostic_WhenNamespaceIsImported()
    {
        string source = "#using scripts\\util;\n#namespace game;\nfunction run()\n{\n    util::helper();\n}\n";

        ImmutableArray<Diagnostic> diagnostics = Lint(source);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void NoDiagnostic_ForOwnNamespaceOrUnqualifiedCalls()
    {
        string source = "#namespace util;\nfunction run()\n{\n    helper();\n    util::helper();\n}\n";

        ImmutableArray<Diagnostic> diagnostics = Lint(source);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Suppressed_WhenAUsingCannotBeResolved()
    {
        // The unresolved #using could contribute the namespace, so we must not flag it.
        string source = "#using scripts\\missing;\n#namespace game;\nfunction run()\n{\n    util::helper();\n}\n";

        ImmutableArray<Diagnostic> diagnostics = Lint(source);

        Assert.Empty(diagnostics);
    }

    /// <summary>The same rule asked about a merge dialect, which has no <c>#using</c> to satisfy it.</summary>
    private static ImmutableArray<Diagnostic> LintAsCod4(string askingSource)
    {
        using TestWorkspace workspace = TestWorkspace.Build([new TestFile(@"myutils.gsc", "func()\n{\n}\n")], s_cod4);

        return NamespaceUsageLint.Analyze(
            workspace.Analyze(@"maps\mp\_menus.gsc", askingSource), workspace.Database.Gsc, ScriptLanguage.Gsc,
            workspace.Resolver, TestPaths.Raw(@"maps\mp\_menus.gsc"), "raw", s_cod4);
    }

    [Fact]
    public void NoDiagnostic_OnAnIncludeDialect()
    {
        // 5000 is unsatisfiable where #using does not exist. The directive does not even lex on an
        // IW profile, and #namespace is off too, so the available set can only ever hold the file's
        // own stem — no edit the user could make would clear the Error. The call links: CoD4 keys a
        // function under no namespace, so `myutils::func()` resolves through the #include and the
        // script runs, while the message asks for an import the dialect has no spelling for.
        string source = "#include myutils;\ninit()\n{\n\tmyutils::func();\n}\n";

        Assert.Empty(LintAsCod4(source));
    }

    [Fact]
    public void Reports_WhenTheUnimportedCallCameOutOfAMacroBody()
    {
        // The reported case: nothing in the file spells `util::` — a macro does — and the import is
        // just as required, because the preprocessor runs first and what links is the expansion.
        // Before ReferenceEntry.FromMacro existed the expansion overwrote the reference's kind, so
        // this rule (which asks for Call) could not see the call at all.
        string source =
            "#define HELP() util::helper()\n#namespace game;\nfunction run()\n{\n    HELP();\n}\n";

        ImmutableArray<Diagnostic> diagnostics = Lint(source);

        Assert.Single(diagnostics);
        Assert.Equal(GscDiagnosticCode.NamespaceNotImported, diagnostics[0].Code);
    }

    [Fact]
    public void TheMacroReport_LandsOnTheInvocation_NotInsideTheDefine()
    {
        // The macro's name is the only text on screen, so that is where the squiggle belongs — and
        // it is where the add-#using fix is offered, which is derived from the same entry.
        string source =
            "#define HELP() util::helper()\n#namespace game;\nfunction run()\n{\n    HELP();\n}\n";

        Diagnostic report = Lint(source)[0];

        Assert.Equal(4, report.Range.Start.Line);
    }

    [Fact]
    public void OneReportPerNamespace_WhenAMacroBodyCallsIntoItTwice()
    {
        // Every call in the body keys to the same invocation range, so without the (range,
        // namespace) guard a two-call macro stacks two identical Errors on one word.
        string source =
            "#define HELP() util::helper(); util::helper()\n#namespace game;\nfunction run()\n{\n    HELP();\n}\n";

        Assert.Single(Lint(source));
    }

    [Fact]
    public void NoDiagnostic_WhenTheMacroBodysNamespaceIsImported()
    {
        string source =
            "#using scripts\\util;\n#define HELP() util::helper()\n#namespace game;\nfunction run()\n{\n    HELP();\n}\n";

        Assert.Empty(Lint(source));
    }
}
