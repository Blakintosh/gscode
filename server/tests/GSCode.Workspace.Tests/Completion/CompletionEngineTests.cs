using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Workspace.Api;
using GSCode.Workspace.Completion;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Tests.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Completion;

public class CompletionEngineTests
{
    private const string Raw = @"C:\bo3\share\raw";
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static (CompletionEngine Engine, ScriptDatabase Db, PathResolver Resolver) BuildWorld(FakeFileSystem files)
    {
        RootConfig config = RootConfig.Create(true, null, null, @"C:\bo3", [], files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, files, new NameTable());
        indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None).GetAwaiter().GetResult();

        CompletionEngine engine = new(database, BuiltinApiSet.Load(ApiDirectory), ObjectFields.Load(ApiDirectory));
        return (engine, database, resolver);
    }

    private static ParseResult Analyze(string path, string text)
    {
        return ScriptAnalysis.Analyze(path, ScriptAnalysis.LanguageFromPath(path), SourceText.From(text), GSCode.Parser.Preprocessing.NullInsertProvider.Instance, new NameTable());
    }

    private static bool HasLabel(ImmutableArray<CompletionEntry> entries, string label)
    {
        return entries.Any(e => string.Equals(e.Label, label, StringComparison.Ordinal));
    }

    [Fact]
    public void NamespaceQualified_OffersOnlyThatNamespacesFunctions()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\util.gsc", "#namespace util;\nfunction alpha()\n{\n}\nfunction beta()\n{\n}\n")
            .AddFile(@$"{Raw}\scripts\other.gsc", "#namespace other;\nfunction gamma()\n{\n}\n");

        (CompletionEngine engine, _, _) = BuildWorld(files);

        // "util::" — cursor right after the ::.
        string text = "#namespace game;\nfunction run()\n{\n    util::\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);
        Position after = new(3, 10); // just past "util::"

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", after);

        Assert.True(HasLabel(entries, "alpha"));
        Assert.True(HasLabel(entries, "beta"));
        Assert.False(HasLabel(entries, "gamma"));
    }

    [Fact]
    public void Keywords_CarryDocumentation_AndAssertIsNotAKeyword()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\dummy.gsc", "function d()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "function run()\n{\n    \n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);
        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(2, 4));

        CompletionEntry isdefined = entries.First(e => e.Label == "isdefined" && e.Kind == CompletionKind.Keyword);
        Assert.Contains("undefined", isdefined.Documentation);

        // assert / assertmsg are engine builtins, not keywords — they must not appear as keyword items.
        Assert.DoesNotContain(entries, e => e.Kind == CompletionKind.Keyword && e.Label == "assert");
        Assert.DoesNotContain(entries, e => e.Kind == CompletionKind.Keyword && e.Label == "assertmsg");
    }

    [Fact]
    public void InsideStringLiteral_OffersKnownStringLiterals()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\events.gsc", "#namespace ev;\nfunction fire()\n{\n    self notify( \"player_spawned\" );\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        // main.gsc: cursor inside the empty string on line 3 (between the quotes).
        string text = "#namespace game;\nfunction run()\n{\n    x = \"\";\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);
        Position insideString = new(3, 9);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", insideString);

        Assert.True(HasLabel(entries, "player_spawned"));
        Assert.All(entries, e => Assert.Equal(CompletionKind.Literal, e.Kind));
    }

    [Fact]
    public void InsideStringLiteral_OffersNothing_WhenLiteralsDisabled()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\events.gsc", "#namespace ev;\nfunction fire()\n{\n    self notify( \"player_spawned\" );\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "#namespace game;\nfunction run()\n{\n    x = \"\";\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);
        Position insideString = new(3, 9);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", insideString, includeLiterals: false);

        Assert.Empty(entries);
    }

    [Fact]
    public void StatementScope_OffersKeywordsMacrosAndBuiltins()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\dummy.gsc", "function d()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "#define CAP 5\nfunction run()\n{\n    \n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);
        Position inside = new(3, 4);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", inside);

        Assert.True(HasLabel(entries, "if"));
        Assert.True(HasLabel(entries, "foreach"));
        Assert.True(HasLabel(entries, "CAP"));       // file-local macro
        Assert.True(HasLabel(entries, "IPrintLn") || entries.Any(e => e.Detail == "builtin"));
    }

    [Fact]
    public void TopLevel_OffersDeclarationKeywords()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\dummy.gsc", "function d()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "#namespace game;\n\nfunction run()\n{\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);
        Position topLevel = new(1, 0);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", topLevel);

        Assert.True(HasLabel(entries, "function"));
        Assert.True(HasLabel(entries, "class"));
        Assert.False(HasLabel(entries, "if"));
    }

    [Fact]
    public void MemberAccess_OffersFieldsAndSize()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\dummy.gsc", "function d()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "function run()\n{\n    self.health = 1;\n    x = self.\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);
        Position afterDot = new(3, 13); // just past "self."

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", afterDot);

        Assert.True(HasLabel(entries, "health"));
        Assert.True(HasLabel(entries, "size"));
    }

    [Fact]
    public void PrecacheArgument_OffersAssetTypes()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\dummy.gsc", "function d()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "#precache( \n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);
        Position afterParen = new(0, 11);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", afterParen);

        Assert.Contains(entries, e => e.Kind == CompletionKind.AssetType && e.Label == "model");
        Assert.Contains(entries, e => e.Kind == CompletionKind.AssetType && e.Label == "string");
    }
}
