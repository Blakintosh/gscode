using GSCode.Core;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Api;
using GSCode.Workspace.Completion;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Tests.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Completion;

public class SignatureEngineTests
{
    private const string Raw = @"C:\bo3\share\raw";
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static SignatureEngine BuildEngine(FakeFileSystem files)
    {
        RootConfig config = RootConfig.Create(true, @"C:\bo3\share\raw", @"C:\bo3\mods", [], files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, files, new NameTable());
        indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None).GetAwaiter().GetResult();
        return new SignatureEngine(database, BuiltinApiSet.Load(ApiDirectory));
    }

    private static ParseResult Analyze(string path, string text)
    {
        return ScriptAnalysis.Analyze(path, ScriptAnalysis.LanguageFromPath(path), SourceText.From(text), NullInsertProvider.Instance, new NameTable());
    }

    [Fact]
    public void ScriptFunction_ShowsParametersAndActiveIndex()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\util.gsc", "#namespace util;\nfunction give( weapon, ammo )\n{\n}\n");
        SignatureEngine engine = BuildEngine(files);

        // Cursor after the comma -> active parameter 1 (ammo).
        string text = "function run()\n{\n    util::give( \"x\", \n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);
        Position afterComma = new(2, 20);

        SignatureResult? signature = engine.Resolve(result, "raw", afterComma);

        Assert.NotNull(signature);
        Assert.Equal(2, signature!.Parameters.Length);
        Assert.Equal("weapon", signature.Parameters[0].Label);
        Assert.Equal("ammo", signature.Parameters[1].Label);
        Assert.Equal(1, signature.ActiveParameter);
    }

    [Fact]
    public void Builtin_ResolvesSignature()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\d.gsc", "function d()\n{\n}\n");
        SignatureEngine engine = BuildEngine(files);

        string text = "function run()\n{\n    x = Abs( \n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);
        Position afterParen = new(2, 12);

        SignatureResult? signature = engine.Resolve(result, "raw", afterParen);

        Assert.NotNull(signature);
        Assert.StartsWith("Abs(", signature!.Label, StringComparison.Ordinal);
        Assert.Single(signature.Parameters);
    }

    [Fact]
    public void OutsideCall_ReturnsNull()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\d.gsc", "function d()\n{\n}\n");
        SignatureEngine engine = BuildEngine(files);

        string text = "function run()\n{\n    x = 1;\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);
        Position notInCall = new(2, 9);

        Assert.Null(engine.Resolve(result, "raw", notInCall));
    }
}

