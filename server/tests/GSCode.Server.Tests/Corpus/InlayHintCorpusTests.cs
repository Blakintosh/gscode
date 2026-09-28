using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Configuration;
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
/// What the inlay hints actually SAY over real scripts, and whether scrolling changes it.
///
/// <c>HandlerCostTests</c> already times this handler and <c>InlayHintParameterTests</c> pins its
/// behaviour on one hand-written file. Neither answers the question that went wrong twice on this
/// branch: how many labels come out of a whole game's scripts, and does the same file answer
/// differently when only part of it is on screen.
///
/// Three things are asserted, each of them a regression this branch already had:
///
/// <list type="bullet">
/// <item>EVERY GAME PRODUCES BOTH FAMILIES. The merge dialects lost their parameter names entirely
/// and the suite stayed green, because nothing counted them per game. A zero here is that bug.</item>
/// <item>THE WINDOW ONLY HIDES. The hints for a visible range must be exactly the whole-file hints
/// that fall inside it — the pruning that makes scrolling cheap must not change the answer.</item>
/// <item>NO LABEL REPEATS THE WORD UNDER IT. <c>apply( who: who )</c> is the suppression in place;
/// counted from the source text rather than the tree, so it is an independent answer.</item>
/// </list>
///
/// The totals themselves are reported, not asserted, for <c>CorpusDiagnosticSweepTests</c>'s
/// reason: they move whenever the resolver learns a new callee form, and a number nobody can
/// justify updating is a number people update without reading.
///
/// The macro family is left at its default (off), since the point is what a user sees.
/// </summary>
[Trait("Category", "Corpus")]
[Collection(GameProfileCollection.Name)]
public class InlayHintCorpusTests
{
    /// <summary>
    /// How many files to hint, per game.
    ///
    /// <c>CorpusFixture.FormatterSampleSize</c>'s number, for its reason: each file here pays a
    /// parse, a whole-file flow pass and two handler requests, and the whole corpus would turn a
    /// gate into an overnight run. Recall comes from spreading the sample, not from growing it.
    /// </summary>
    private const int SampleSize = 250;

    /// <summary>
    /// How many lines the simulated viewport covers. A screenful, since the pruning under test is
    /// the one that runs per visible range.
    /// </summary>
    private const int WindowLines = 40;

    /// <summary>How many disagreements to name before a failure message stops being read.</summary>
    private const int ExamplesShown = 10;

    private readonly ITestOutputHelper _output;

    public InlayHintCorpusTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>One label the handler produced, flattened to what a comparison needs.</summary>
    private sealed record Hint(int Line, int Character, string Label, InlayHintKind Kind)
    {
        public override string ToString()
        {
            return $"{Line + 1}:{Character + 1} {Label}";
        }
    }

    /// <summary>What one game's sweep found.</summary>
    private sealed class Totals
    {
        public int Files { get; set; }
        public int TypeHints { get; set; }
        public int ParameterHints { get; set; }
        public int SelfNamed { get; set; }
        public int WindowDisagreements { get; set; }
        public string? DensestFile { get; set; }
        public int DensestCount { get; set; }
        public Dictionary<string, int> Labels { get; } = new(StringComparer.Ordinal);
        public List<string> Examples { get; } = [];
    }

    [Fact]
    public async Task Hints_OverTheCorpus()
    {
        bool swept = false;

        // Collected rather than thrown as they are found, so one dialect's regression does not hide
        // the other's: the two families this asserts are exactly the pair that went wrong on ONE
        // dialect while the other stayed right, and a first failure that ends the run reports half
        // the answer.
        List<string> failures = [];

        if ( CorpusFixture.Available )
        {
            await SweepAsync(GameProfile.BlackOps3, CorpusFixture.Resolver, CorpusFixture.Scripts, failures);
            swept = true;
        }

        GameCorpus? cod4 = GameCorpusFixture.For(GameProfile.Cod4);
        if ( cod4 is not null )
        {
            GameCorpus captured = cod4;
            await SweepAsync(
                captured.Profile,
                () => GameCorpusFixture.Resolver(captured),
                () => GameCorpusFixture.Scripts(captured),
                failures);

            swept = true;
        }

        if ( !swept )
        {
            _output.WriteLine("SKIPPED: neither %GSCODE_CORPUS_BO3% nor %GSCODE_CORPUS_COD4% found.");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private async Task SweepAsync(
        GameProfile profile,
        Func<PathResolver> resolverFactory,
        Func<IReadOnlyList<string>> scriptsFactory,
        List<string> failures)
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

            // A FINISHED index. Parameter names come from resolving the callee, which is a store
            // query per declared namespace — against a half-built index most calls resolve to
            // nothing, and the sweep would report a shortfall it invented itself.
            WorkspaceIndexer indexer = new(database, () => resolver, new PhysicalFileSystem(), names);
            await indexer.IndexAsync(IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None);

            string apiDirectory = Path.Combine(AppContext.BaseDirectory, "Api");
            BuiltinApiSet builtins = BuiltinApiSet.Load(apiDirectory);
            ObjectFields objectFields = ObjectFields.Load(apiDirectory);

            Totals totals = new();
            foreach ( string path in Sample(scriptsFactory()) )
            {
                await SweepFileAsync(path, database, resolver, names, builtins, objectFields, totals);
            }

            Report(profile, totals);
            Verify(profile, totals, failures);
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
        }
    }

    /// <summary>
    /// The sample: evenly spaced through the sorted file list.
    ///
    /// NOT the first N. The corpus sorts by path, so the first 250 files are one or two folders —
    /// one subsystem, written by one person, with one set of naming habits. Spreading the stride
    /// costs nothing and is the difference between sampling a game and sampling a directory.
    /// </summary>
    private static IReadOnlyList<string> Sample(IReadOnlyList<string> scripts)
    {
        if ( scripts.Count <= SampleSize )
        {
            return scripts;
        }

        int stride = scripts.Count / SampleSize;
        List<string> sampled = [];

        for ( int index = 0; index < scripts.Count && sampled.Count < SampleSize; index += stride )
        {
            sampled.Add(scripts[index]);
        }

        return sampled;
    }

    private static async Task SweepFileAsync(
        string path,
        ScriptDatabase database,
        PathResolver resolver,
        NameTable names,
        BuiltinApiSet builtins,
        ObjectFields objectFields,
        Totals totals)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch ( IOException )
        {
            return;
        }

        ResolverHolder holder = new(new PhysicalFileSystem()) { Current = resolver };
        DocumentStore documents = new(
            candidate => new ResolverInsertProvider(resolver, resolver.GetContext(candidate), new PhysicalFileSystem(), new InsertCache()),
            names);

        OpenDocument document = documents.Open(path, text, version: 1);
        ParseResult parsed = documents.AnalyzeIfStale(document);

        NavigationSupport support = new(documents, database, holder);
        TextDocumentSelector selector = TextDocumentSelector.ForLanguage("gsc");
        ServerSettings settings = new() { InlayParameterNames = true, InlayInferredTypes = true };

        TextDocumentIdentifier identifier = new() { Uri = DocumentUri.FromFileSystemPath(path) };
        InlayHintHandler handler = new(support, builtins, objectFields, settings, selector);

        CorePosition end = parsed.Text.GetPosition(parsed.Text.Length);
        IReadOnlyList<Hint> whole = await RequestAsync(handler, identifier, new LspRange(0, 0, end.Line, end.Character));

        totals.Files++;
        Count(parsed, whole, path, totals);

        // One screenful a third of the way down: far enough in that calls and assignments straddle
        // both edges of it, which is where the pruning has to be right.
        int windowStart = end.Line / 3;
        int windowEnd = Math.Min(windowStart + WindowLines, end.Line + 1);
        IReadOnlyList<Hint> windowed = await RequestAsync(handler, identifier, new LspRange(windowStart, 0, windowEnd, 0));

        CompareWindow(path, whole, windowed, windowStart, windowEnd, totals);
    }

    private static async Task<IReadOnlyList<Hint>> RequestAsync(
        InlayHintHandler handler, TextDocumentIdentifier identifier, LspRange range)
    {
        InlayHintContainer? container = await handler.Handle(
            new InlayHintParams { TextDocument = identifier, Range = range }, CancellationToken.None);

        if ( container is null )
        {
            return [];
        }

        List<Hint> hints = [];
        foreach ( InlayHint hint in container )
        {
            hints.Add(new Hint(
                hint.Position.Line,
                hint.Position.Character,
                hint.Label.String ?? string.Empty,
                hint.Kind ?? InlayHintKind.Type));
        }

        return hints;
    }

    private static void Count(ParseResult parsed, IReadOnlyList<Hint> hints, string path, Totals totals)
    {
        foreach ( Hint hint in hints )
        {
            if ( hint.Kind != InlayHintKind.Parameter )
            {
                totals.TypeHints++;
                continue;
            }

            totals.ParameterHints++;

            string name = hint.Label.TrimEnd(':');
            totals.Labels[name] = totals.Labels.TryGetValue(name, out int seen) ? seen + 1 : 1;

            string? written = BareIdentifierAt(parsed.Text, new CorePosition(hint.Line, hint.Character));
            if ( written is not null && string.Equals(written, name, StringComparison.OrdinalIgnoreCase) )
            {
                totals.SelfNamed++;
                if ( totals.Examples.Count < ExamplesShown )
                {
                    totals.Examples.Add($"says its own name: {Path.GetFileName(path)}({hint.Line + 1}) {name}: {written}");
                }
            }
        }

        if ( hints.Count > totals.DensestCount )
        {
            totals.DensestCount = hints.Count;
            totals.DensestFile = path;
        }
    }

    /// <summary>
    /// The windowed request against the whole-file one, restricted to the same span.
    ///
    /// The restriction is half-open exactly as <see cref="TextRange.Contains"/> is, and both edges
    /// sit at character 0 of a line so no name can be half in and half out — the type family tests
    /// where a name STARTS and its label lands at the name's end, which agree only when the window
    /// cannot split a line.
    /// </summary>
    private static void CompareWindow(
        string path,
        IReadOnlyList<Hint> whole,
        IReadOnlyList<Hint> windowed,
        int windowStart,
        int windowEnd,
        Totals totals)
    {
        HashSet<Hint> expected = [];
        foreach ( Hint hint in whole )
        {
            if ( hint.Line >= windowStart && hint.Line < windowEnd )
            {
                expected.Add(hint);
            }
        }

        HashSet<Hint> actual = [.. windowed];

        foreach ( Hint hint in expected )
        {
            if ( !actual.Contains(hint) )
            {
                totals.WindowDisagreements++;
                if ( totals.Examples.Count < ExamplesShown )
                {
                    totals.Examples.Add($"dropped when windowed: {Path.GetFileName(path)}({hint})");
                }
            }
        }

        foreach ( Hint hint in actual )
        {
            if ( !expected.Contains(hint) )
            {
                totals.WindowDisagreements++;
                if ( totals.Examples.Count < ExamplesShown )
                {
                    totals.Examples.Add($"only when windowed: {Path.GetFileName(path)}({hint})");
                }
            }
        }
    }

    /// <summary>
    /// The bare identifier written at a position, or null when what is there is not one.
    ///
    /// Read from the SOURCE TEXT, not the tree. The handler decides this from the AST, so asking
    /// the tree the same question would agree with the handler even when both are wrong; the text
    /// is what the user is looking at, which is what the label has to earn its space against.
    ///
    /// Bare means the identifier IS the whole argument. A field access, an index, a nested call or
    /// a qualifier makes the label say something the word under it does not, which is the case the
    /// suppression deliberately leaves alone.
    /// </summary>
    private static string? BareIdentifierAt(SourceText text, CorePosition position)
    {
        string source = text.Text;
        int offset = text.GetOffset(position);
        if ( offset < 0 || offset >= source.Length )
        {
            return null;
        }

        int end = offset;
        while ( end < source.Length && (char.IsLetterOrDigit(source[end]) || source[end] == '_') )
        {
            end++;
        }

        if ( end == offset )
        {
            return null;
        }

        int after = end;
        while ( after < source.Length && (source[after] == ' ' || source[after] == '\t') )
        {
            after++;
        }

        if ( after >= source.Length || (source[after] != ',' && source[after] != ')') )
        {
            return null;
        }

        return source.Substring(offset, end - offset);
    }

    private void Report(GameProfile profile, Totals totals)
    {
        _output.WriteLine($"=== {profile.ShortName}: inlay hints over {totals.Files} sampled files ===");
        _output.WriteLine($"  parameter names : {totals.ParameterHints}");
        _output.WriteLine($"  inferred types  : {totals.TypeHints}");
        _output.WriteLine($"  densest file    : {totals.DensestCount} hints in {Path.GetFileName(totals.DensestFile ?? "-")}");

        List<KeyValuePair<string, int>> ranked = [.. totals.Labels];
        ranked.Sort(static (left, right) => right.Value.CompareTo(left.Value));

        _output.WriteLine("  most frequent labels:");
        foreach ( KeyValuePair<string, int> entry in ranked.Take(10) )
        {
            _output.WriteLine($"    {entry.Value,6}x {entry.Key}:");
        }

        foreach ( string example in totals.Examples )
        {
            _output.WriteLine($"  {example}");
        }
    }

    private static void Verify(GameProfile profile, Totals totals, List<string> failures)
    {
        if ( totals.Files == 0 )
        {
            return;
        }

        if ( totals.ParameterHints == 0 )
        {
            failures.Add(
                $"{profile.ShortName}: not one parameter-name hint over {totals.Files} files. Either the family is "
                + "off for this dialect, or callee resolution answers nothing here.");
        }

        if ( totals.TypeHints == 0 )
        {
            failures.Add(
                $"{profile.ShortName}: not one inferred-type hint over {totals.Files} files. The flow typer inferred "
                + "nothing displayable for this dialect.");
        }

        if ( totals.SelfNamed > 0 )
        {
            failures.Add(
                $"{profile.ShortName}: {totals.SelfNamed} labels repeat the word they sit on. "
                + string.Join("; ", totals.Examples.Take(ExamplesShown)));
        }

        if ( totals.WindowDisagreements > 0 )
        {
            failures.Add(
                $"{profile.ShortName}: {totals.WindowDisagreements} hints differ between a whole-file request and a "
                + $"{WindowLines}-line window. Scrolling changes what the file says. "
                + string.Join("; ", totals.Examples.Take(ExamplesShown)));
        }
    }
}
