using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Workspace.Api;
using GSCode.Workspace.Completion;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Tests.Resolution;
using Xunit;
using Xunit.Abstractions;

namespace GSCode.Workspace.Tests.Completion;

/// <summary>
/// Completion as it is actually reached: with the name PARTIALLY TYPED, which is the only way a
/// user ever sees the list. The other tests complete at an empty position, where the current-word
/// index is -1 and a different code path picks the scan's starting point.
/// </summary>
public class RealisticKeystrokeTests
{
    private const string Raw = @"C:\bo3\share\raw";

    private readonly ITestOutputHelper _output;

    public RealisticKeystrokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static CompletionEngine Build()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\util.gsc", "#namespace util;\nfunction foobar()\n{\n}\n");

        RootConfig config = RootConfig.Create(true, null, null, @"C:\bo3", [], files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, files, new NameTable());
        indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None)
            .GetAwaiter().GetResult();

        string api = Path.Combine(AppContext.BaseDirectory, "Api");
        return new CompletionEngine(database, BuiltinApiSet.Load(api), ObjectFields.Load(api));
    }

    private static string InsertTextFor(string line)
    {
        CompletionEngine engine = Build();
        string text = "#namespace util;\n\nfunction run()\n{\n" + line + "\n}\n";

        ParseResult result = ScriptAnalysis.Analyze(
            @$"{Raw}\scripts\main.gsc",
            ScriptLanguage.Gsc,
            SourceText.From(text),
            GSCode.Parser.Preprocessing.NullInsertProvider.Instance,
            new NameTable());

        ImmutableArray<CompletionEntry> entries = engine.Complete(
            result, "raw", new Position(4, line.Length), callPunctuation: CallPunctuation.ParensAndSemicolon);

        CompletionEntry entry = Assert.Single(entries, e => e.Label == "foobar");
        return entry.InsertText;
    }

    [Theory]
    [InlineData("    foob")]
    [InlineData("    self foob")]
    [InlineData("    x = foob")]
    [InlineData("    self thread foob")]
    [InlineData("    self util::foob")]
    public void APartiallyTypedStatementCallStillGetsItsSemicolon(string line)
    {
        string insertText = InsertTextFor(line);

        _output.WriteLine($"[{line}] -> {insertText}");
        Assert.Equal("foobar($0);", insertText);
    }
}
