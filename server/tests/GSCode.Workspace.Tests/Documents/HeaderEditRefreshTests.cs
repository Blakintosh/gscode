using System.Linq;
using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Tests.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Documents;

/// <summary>
/// An open document's analysis goes stale when a header it #inserts changes, not only when its own
/// text does.
///
/// Reported as a hover bug: edit a value in a GSH, hover the macro in a GSC that inserts it, and
/// the old value is still shown — until any keystroke in the GSC, which updates it. The keystroke
/// is the tell. Hover answers from the document's LATEST completed analysis, and staleness was
/// measured against the document's own text alone, so a parse that expanded the OLD header
/// reported itself current forever. Typing changed the text, which forced the re-analysis that
/// picked the new header up.
/// </summary>
public class HeaderEditRefreshTests
{
    private const string Raw = @"C:\bo3\share\raw";
    private const string GshPath = @$"{Raw}\scripts\shared\shared.gsh";
    private const string GscPath = @$"{Raw}\scripts\uses_it.gsc";
    private const string Dependent = "#insert scripts\\shared\\shared.gsh;\nfunction f()\n{\n    x = CAP;\n}\n";

    private static (DocumentStore Store, InsertCache Inserts, FakeFileSystem Files) Build()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(GshPath, "#define CAP 5\n")
            .AddFile(GscPath, Dependent);

        RootConfig config = RootConfig.Create(true, Raw, @"C:\bo3\mods", [], files);
        PathResolver resolver = new(config, files);
        InsertCache inserts = new();

        DocumentStore store = new(
            path => new ResolverInsertProvider(resolver, resolver.GetContext(path), files, inserts),
            new NameTable(),
            inserts);

        return (store, inserts, files);
    }

    /// <summary>The body of the named macro as source text, e.g. "5".</summary>
    private static string MacroBody(ParseResult result, string name)
    {
        Assert.True(result.Preprocessed.Macros.TryGet(name, out MacroDefinition macro));
        return string.Concat(macro.Body.Select(token => token.Text));
    }

    [Fact]
    public void TheOpenDependentSeesTheHeaderItWasAnalysedAgainst()
    {
        (DocumentStore store, _, _) = Build();
        OpenDocument document = store.Open(GscPath, Dependent, version: 1);

        Assert.Equal("5", MacroBody(store.Analyze(document), "CAP"));
    }

    [Fact]
    public void AChangedHeaderReanalysesTheOpenDependent()
    {
        // The reported bug. Nothing about the GSC has changed — only the header under it — so the
        // document has to be re-analysed on the strength of the header alone.
        (DocumentStore store, InsertCache inserts, FakeFileSystem files) = Build();
        OpenDocument document = store.Open(GscPath, Dependent, version: 1);
        store.Analyze(document);

        files.AddFile(GshPath, "#define CAP 99\n");
        inserts.Invalidate(PathUtil.NormalizeAbsolute(GshPath));

        Assert.Equal("99", MacroBody(store.AnalyzeIfStale(document), "CAP"));
    }

    [Fact]
    public void TheRepublishedResultIsWhatHoverReads()
    {
        // Hover reads LatestResult, not the value AnalyzeIfStale returns, so the re-analysis has to
        // be PUBLISHED on the document rather than merely computed for the caller.
        (DocumentStore store, InsertCache inserts, FakeFileSystem files) = Build();
        OpenDocument document = store.Open(GscPath, Dependent, version: 1);
        store.Analyze(document);

        files.AddFile(GshPath, "#define CAP 99\n");
        inserts.Invalidate(PathUtil.NormalizeAbsolute(GshPath));
        store.AnalyzeIfStale(document);

        Assert.Equal("99", MacroBody(document.LatestResult!, "CAP"));
    }

    [Fact]
    public void AChangeToANestedHeaderReachesTheOpenDependentToo()
    {
        // A header inserted through ANOTHER header. Re-parsing is not enough on its own here: the
        // wrapper's cached contribution carries copies of the macros the inner header defined, so
        // dropping the inner one alone leaves the re-parse replaying the values it just discarded.
        const string WrapperPath = @$"{Raw}\scripts\shared\wrapper.gsh";
        const string BasePath = @$"{Raw}\scripts\shared\base.gsh";
        const string ThroughWrapper = "#insert scripts\\shared\\wrapper.gsh;\nfunction f()\n{\n    x = CAP;\n}\n";

        FakeFileSystem files = new FakeFileSystem()
            .AddFile(BasePath, "#define CAP 5\n")
            .AddFile(WrapperPath, "#insert scripts\\shared\\base.gsh;\n")
            .AddFile(GscPath, ThroughWrapper);

        RootConfig config = RootConfig.Create(true, Raw, @"C:\bo3\mods", [], files);
        PathResolver resolver = new(config, files);
        InsertCache inserts = new();
        DocumentStore store = new(
            path => new ResolverInsertProvider(resolver, resolver.GetContext(path), files, inserts),
            new NameTable(),
            inserts);

        OpenDocument document = store.Open(GscPath, ThroughWrapper, version: 1);
        Assert.Equal("5", MacroBody(store.Analyze(document), "CAP"));

        files.AddFile(BasePath, "#define CAP 99\n");
        inserts.Invalidate(PathUtil.NormalizeAbsolute(BasePath));

        Assert.Equal("99", MacroBody(store.AnalyzeIfStale(document), "CAP"));
    }

    [Fact]
    public void AnUntouchedHeaderCostsNoSecondAnalysis()
    {
        // The other half: the refresh must not turn every AnalyzeIfStale into a re-parse. Nothing
        // has changed here, so the cached result stands.
        (DocumentStore store, _, _) = Build();
        OpenDocument document = store.Open(GscPath, Dependent, version: 1);
        ParseResult first = store.Analyze(document);

        Assert.Same(first, store.AnalyzeIfStale(document));
    }
}
