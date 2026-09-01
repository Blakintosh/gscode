using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Workspace.Analysis;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Tests.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Analysis;

/// <summary>
/// The same `namespace::name` declared in two files a script imports: the call is ambiguous, and
/// nothing in the source says which definition it reaches.
///
/// The rule is scoped to the file's OWN imports. Namespaces merge across files, so the same name
/// legitimately exists in places that never meet — the stock scripts hold 565 such pairs, mostly
/// one game mode's copy against another's. Scoping cuts that to 9, and the survivors are real:
/// `_dogs.gsc` imports both `scripts\shared\util_shared` and `scripts\mp\_util`, and both declare
/// `util::wait_endon`.
/// </summary>
public class AmbiguousFunctionLintTests
{
    private const string Raw = @"C:\bo3\share\raw";

    private static ImmutableArray<Diagnostic> Lint(FakeFileSystem files, string source)
    {
        RootConfig config = RootConfig.Create(true, @"C:\bo3\share\raw", @"C:\bo3\mods", [], files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, files, new NameTable());
        indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None)
            .GetAwaiter().GetResult();

        string path = @$"{Raw}\scripts\main.gsc";
        ParseResult result = ScriptAnalysis.Analyze(
            path, ScriptLanguage.Gsc, SourceText.From(source), GSCode.Parser.Preprocessing.NullInsertProvider.Instance, new NameTable());

        return AmbiguousFunctionLint.Analyze(result, database.Gsc, ScriptLanguage.Gsc, resolver, path);
    }

    /// <summary>Two files, both declaring `util::helper`.</summary>
    private static FakeFileSystem TwoProviders()
    {
        return new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\shared\util_shared.gsc", "#namespace util;\nfunction helper()\n{\n}\n")
            .AddFile(@$"{Raw}\scripts\mp\_util.gsc", "#namespace util;\nfunction helper()\n{\n}\nfunction only_here()\n{\n}\n");
    }

    [Fact]
    public void ImportingBothProvidersMakesTheCallAmbiguous()
    {
        ImmutableArray<Diagnostic> diagnostics = Lint(
            TwoProviders(),
            "#using scripts\\shared\\util_shared;\n#using scripts\\mp\\_util;\n"
            + "function run()\n{\n    util::helper();\n}\n");

        Diagnostic ambiguous = Assert.Single(diagnostics);

        Assert.Equal(GscDiagnosticCode.AmbiguousFunction, ambiguous.Code);
        Assert.Equal(DiagnosticSeverity.Warning, ambiguous.Severity);
        Assert.Contains("helper", ambiguous.Message, StringComparison.Ordinal);
        Assert.Contains("util", ambiguous.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ItPointsAtEveryDefinition()
    {
        // Related information is the whole value here: knowing it is ambiguous without being told
        // where the definitions are leaves the reader to grep for them.
        ImmutableArray<Diagnostic> diagnostics = Lint(
            TwoProviders(),
            "#using scripts\\shared\\util_shared;\n#using scripts\\mp\\_util;\n"
            + "function run()\n{\n    util::helper();\n}\n");

        Assert.Equal(2, Assert.Single(diagnostics).RelatedInformation.Length);
    }

    [Fact]
    public void ImportingOnlyOneProviderIsFine()
    {
        // The other declaration exists in the workspace but is not linked here — the case that
        // makes a workspace-wide rule unusable.
        Assert.Empty(Lint(
            TwoProviders(),
            "#using scripts\\shared\\util_shared;\n"
            + "function run()\n{\n    util::helper();\n}\n"));
    }

    [Fact]
    public void ANameOnlyOneProviderDeclaresIsFine()
    {
        Assert.Empty(Lint(
            TwoProviders(),
            "#using scripts\\shared\\util_shared;\n#using scripts\\mp\\_util;\n"
            + "function run()\n{\n    util::only_here();\n}\n"));
    }

    [Fact]
    public void AnAmbiguousNameThatIsNeverCalledIsFine()
    {
        // Reported at the CALL: two definitions that nothing here reaches are not this file's
        // problem, and flagging the imports would be noise.
        Assert.Empty(Lint(
            TwoProviders(),
            "#using scripts\\shared\\util_shared;\n#using scripts\\mp\\_util;\n"
            + "function run()\n{\n}\n"));
    }

    [Fact]
    public void AnUnresolvableImportSuppressesThePass()
    {
        // A definition from a file we could not read might be the one that makes a name
        // ambiguous, or the one that makes it fine.
        Assert.Empty(Lint(
            TwoProviders(),
            "#using scripts\\shared\\util_shared;\n#using scripts\\mp\\_util;\n#using scripts\\nope;\n"
            + "function run()\n{\n    util::helper();\n}\n"));
    }

    [Fact]
    public void ACallAMacroExpandedInto_IsAmbiguousToo()
    {
        // Invoking the macro is what brings the call into THIS file, and this file is where the
        // two definitions meet — so a header body naming util::helper is as undecided as writing
        // it out. The warning lands on the invocation, the only text on screen.
        ImmutableArray<Diagnostic> diagnostics = Lint(
            TwoProviders(),
            "#using scripts\\shared\\util_shared;\n#using scripts\\mp\\_util;\n"
            + "#define HELP() util::helper()\nfunction run()\n{\n    HELP();\n}\n");

        Diagnostic ambiguous = Assert.Single(diagnostics);

        Assert.Equal(GscDiagnosticCode.AmbiguousFunction, ambiguous.Code);
        Assert.Equal(5, ambiguous.Range.Start.Line);
    }

    [Fact]
    public void AnAmbiguousCallAMacroMakesTwice_WarnsOnce()
    {
        ImmutableArray<Diagnostic> diagnostics = Lint(
            TwoProviders(),
            "#using scripts\\shared\\util_shared;\n#using scripts\\mp\\_util;\n"
            + "#define HELP() util::helper(); util::helper()\nfunction run()\n{\n    HELP();\n}\n");

        Assert.Single(diagnostics);
    }
}
