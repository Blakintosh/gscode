using System.Diagnostics;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Server.Configuration;
using GSCode.Server.Formatting;
using GSCode.Server.Handlers;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using Xunit.Abstractions;
using CorePosition = GSCode.Core.Text.Position;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// What one REQUEST costs, on the files where it costs most.
///
/// PERF.md measures the analysis pipeline and the cross-file lints, and <c>LintBudgetTests</c>
/// bounds a lint against the keystroke debounce. Nothing measured the handlers themselves, and
/// three of them do work whose shape suggests it matters:
///
/// <list type="bullet">
/// <item>CodeLens builds one lens per function, class, method and file-local macro, and each lens
/// runs the full shared workspace reference query — then keeps a count and discards the array.</item>
/// <item>Inlay hints type the WHOLE file with the flow typer on every request, and the client sends
/// one request per visible range, so scrolling produces a request per frame.</item>
/// <item>Formatting lexes the document up to four times and line-splits it about six, and on-type
/// formatting runs the whole chain on every semicolon and closing brace.</item>
/// </list>
///
/// This reports rather than asserts. A budget is worth setting once there is a measurement to set
/// it from, and this is that measurement; the shape is <c>LintBudgetTests</c>'s, including the
/// reason it does not measure through <c>PerfTracker</c> — every method there is
/// <c>[Conditional("GSCODE_INSTRUMENTATION")]</c>, so a Release corpus run would collect nothing
/// and report an empty table that looks like success.
/// </summary>
[Trait("Category", "Corpus")]
[Collection(GameProfileCollection.Name)]
public class HandlerCostTests
{
    /// <summary>
    /// How many of the most declaration-dense files to time.
    ///
    /// The whole corpus is not the question. A handler's cost here scales with what the file
    /// DECLARES — lenses per declaration, hints per call — so the worst case lives in the densest
    /// files, and timing the other nine hundred adds an hour to learn that a small file is small.
    /// </summary>
    private const int SampleSize = 25;

    private readonly ITestOutputHelper _output;

    public HandlerCostTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private sealed record FileCost(
        string Path,
        int Declarations,
        double CodeLensMilliseconds,
        double InlayHintMilliseconds,
        double FormatMilliseconds);

    [Fact]
    public async Task Handlers_WhereTheTimeGoes()
    {
        bool measured = false;

        if ( CorpusFixture.Available )
        {
            await MeasureAsync(GameProfile.BlackOps3, CorpusFixture.Resolver, CorpusFixture.Scripts);
            measured = true;
        }

        GameCorpus? cod4 = GameCorpusFixture.For(GameProfile.Cod4);
        if ( cod4 is not null )
        {
            GameCorpus captured = cod4;
            await MeasureAsync(
                captured.Profile,
                () => GameCorpusFixture.Resolver(captured),
                () => GameCorpusFixture.Scripts(captured));

            measured = true;
        }

        if ( !measured )
        {
            _output.WriteLine("SKIPPED: neither %GSCODE_CORPUS_BO3% nor %GSCODE_CORPUS_COD4% found.");
        }
    }

    private async Task MeasureAsync(
        GameProfile profile, Func<PathResolver> resolverFactory, Func<IReadOnlyList<string>> scriptsFactory)
    {
        // GameProfile.Active is process-global; GameProfileCollection stops this racing another
        // class and restoring it stops the selection leaking into the next test here.
        GameProfile previous = GameProfile.Active;
        try
        {
            GameProfile.Select(profile.ShortName);

            PathResolver resolver = resolverFactory();
            NameTable names = new();
            ScriptDatabase database = new();

            // A FINISHED index, like the lint gate next door: the reference index is what CodeLens
            // queries, and counting against a half-built one measures the wrong thing entirely.
            WorkspaceIndexer indexer = new(database, () => resolver, new PhysicalFileSystem(), names);
            await indexer.IndexAsync(IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None);

            string apiDirectory = Path.Combine(AppContext.BaseDirectory, "Api");
            BuiltinApiSet builtins = BuiltinApiSet.Load(apiDirectory);
            ObjectFields objectFields = ObjectFields.Load(apiDirectory);

            List<FileCost> costs = [];
            foreach ( string path in DensestFiles(database, scriptsFactory()) )
            {
                FileCost? cost = await MeasureFileAsync(path, database, resolver, names, builtins, objectFields);
                if ( cost is not null )
                {
                    costs.Add(cost);
                }
            }

            Report(profile, costs);
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
        }
    }

    /// <summary>
    /// The sample: the indexed files declaring the most, since that is what these handlers pay per.
    /// </summary>
    private static IEnumerable<string> DensestFiles(ScriptDatabase database, IReadOnlyList<string> scripts)
    {
        List<(string Path, int Declarations)> ranked = [];

        foreach ( string path in scripts )
        {
            if ( !database.TryGetAnyRecord(path, out ScriptRecord record) )
            {
                continue;
            }

            ranked.Add((path, DeclarationCount(record)));
        }

        ranked.Sort(static (left, right) => right.Declarations.CompareTo(left.Declarations));

        foreach ( (string path, int _) in ranked.Take(SampleSize) )
        {
            yield return path;
        }
    }

    private static int DeclarationCount(ScriptRecord record)
    {
        int count = record.Functions.Length + record.Classes.Length + record.Macros.Length;

        foreach ( ClassSymbol classSymbol in record.Classes )
        {
            count += classSymbol.Methods.Length;
        }

        return count;
    }

    private static async Task<FileCost?> MeasureFileAsync(
        string path,
        ScriptDatabase database,
        PathResolver resolver,
        NameTable names,
        BuiltinApiSet builtins,
        ObjectFields objectFields)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch ( IOException )
        {
            return null;
        }

        ResolverHolder holder = new(new PhysicalFileSystem()) { Current = resolver };
        DocumentStore documents = new(
            candidate => new ResolverInsertProvider(resolver, resolver.GetContext(candidate), new PhysicalFileSystem(), new InsertCache()),
            names);

        OpenDocument document = documents.Open(path, text, version: 1);
        ParseResult parsed = documents.AnalyzeIfStale(document);

        NavigationSupport support = new(documents, database, holder);
        TextDocumentSelector selector = TextDocumentSelector.ForLanguage("gsc");

        // Both features are timed with their setting ON, whatever the default is: the question is
        // what the feature costs when someone uses it, not what it costs switched off.
        ServerSettings settings = new() { CodeLensEnabled = true, InlayParameterNames = true, InlayInferredTypes = true };

        DocumentUri uri = DocumentUri.FromFileSystemPath(path);
        TextDocumentIdentifier identifier = new() { Uri = uri };

        CodeLensHandler lenses = new(support, settings, selector);
        InlayHintHandler hints = new(support, builtins, objectFields, settings, selector);

        // One untimed pass each, so the figures are steady-state rather than a measurement of the
        // first call's JIT.
        await lenses.Handle(new CodeLensParams { TextDocument = identifier }, CancellationToken.None);
        await hints.Handle(HintParams(identifier, parsed), CancellationToken.None);
        GscFormatter.FormatMinimalEdits(parsed);

        long started = Stopwatch.GetTimestamp();
        await lenses.Handle(new CodeLensParams { TextDocument = identifier }, CancellationToken.None);
        double lensMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        started = Stopwatch.GetTimestamp();
        await hints.Handle(HintParams(identifier, parsed), CancellationToken.None);
        double hintMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        started = Stopwatch.GetTimestamp();
        GscFormatter.FormatMinimalEdits(parsed);
        double formatMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        int declarations = database.TryGetAnyRecord(path, out ScriptRecord record) ? DeclarationCount(record) : 0;

        return new FileCost(path, declarations, lensMilliseconds, hintMilliseconds, formatMilliseconds);
    }

    /// <summary>
    /// The whole document as the visible range.
    ///
    /// Generous on purpose: a real request covers a screenful. The point is the work the handler
    /// does REGARDLESS of the range — the flow pass over the whole file and the walk of every call
    /// in it — so a whole-file window is the shape that makes the windowing question visible.
    /// </summary>
    private static InlayHintParams HintParams(TextDocumentIdentifier identifier, ParseResult parsed)
    {
        CorePosition end = parsed.Text.GetPosition(parsed.Text.Length);

        return new InlayHintParams
        {
            TextDocument = identifier,
            Range = new LspRange(0, 0, end.Line, end.Character),
        };
    }

    private void Report(GameProfile profile, IReadOnlyList<FileCost> costs)
    {
        if ( costs.Count == 0 )
        {
            _output.WriteLine($"{profile.ShortName}: nothing indexed to measure.");
            return;
        }

        _output.WriteLine($"=== {profile.ShortName}: the {costs.Count} densest files ===");
        _output.WriteLine("  decls   codeLens    inlayHint     format   file");

        foreach ( FileCost cost in costs )
        {
            _output.WriteLine(
                $"  {cost.Declarations,5}  {cost.CodeLensMilliseconds,8:F1}ms  {cost.InlayHintMilliseconds,8:F1}ms  {cost.FormatMilliseconds,8:F1}ms   {Path.GetFileName(cost.Path)}");
        }

        _output.WriteLine(
            $"  worst: codeLens {costs.Max(static c => c.CodeLensMilliseconds):F1}ms, "
            + $"inlayHint {costs.Max(static c => c.InlayHintMilliseconds):F1}ms, "
            + $"format {costs.Max(static c => c.FormatMilliseconds):F1}ms "
            + $"(debounce is {AnalysisTiming.DebounceMilliseconds}ms)");
    }
}
