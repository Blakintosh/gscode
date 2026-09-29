using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Workspace.Api;
using GSCode.Workspace.Completion;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
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

    private readonly ITestOutputHelper _output;

    public RealisticKeystrokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static CompletionEngine Build()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(TestPaths.Raw(@"scripts\util.gsc"), "#namespace util;\nfunction foobar()\n{\n}\n");

        using TestWorkspace workspace = TestWorkspace.Build(files, mode: IndexingMode.Partial);
        ScriptDatabase database = workspace.Database;

        string api = Path.Combine(AppContext.BaseDirectory, "Api");
        return new CompletionEngine(database, BuiltinApiSet.Load(api), ObjectFields.Load(api));
    }

    private static string InsertTextFor(string line)
    {
        CompletionEngine engine = Build();
        string text = "#namespace util;\n\nfunction run()\n{\n" + line + "\n}\n";

        ParseResult result = ScriptAnalysis.Analyze(
            TestPaths.Raw(@"scripts\main.gsc"),
            ScriptLanguage.Gsc,
            SourceText.From(text),
            GSCode.Parser.Preprocessing.NullInsertProvider.Instance,
            new NameTable());

        ImmutableArray<CompletionEntry> entries = engine.Complete(
            result, "raw", new Position(4, line.Length), callPunctuation: CallPunctuation.ParensAndSemicolon);

        // The label carries the parameter list ("foobar()"); this test is about the insert text.
        CompletionEntry entry = Assert.Single(
            entries, e => e.Label == "foobar" || e.Label.StartsWith("foobar(", StringComparison.Ordinal));
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
