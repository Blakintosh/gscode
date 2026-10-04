using System.Diagnostics;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Server.Handlers;
using GSCode.Workspace.Analysis;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using Xunit;
using Xunit.Abstractions;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// The keystroke budget: no single cross-file lint may cost a meaningful share of the debounce on
/// any real script.
///
/// <see cref="AnalysisTiming.DebounceMilliseconds"/> is the whole time an edit gets before its
/// analysis starts, and every rule in <c>WorkspaceLints</c> runs inside it. Nothing bounded an
/// individual rule before this: PERF.md recorded corpus-wide sums, where a rule reading 200 ms
/// means 200 ms spread over 900 files and says nothing about what one keystroke pays. The number
/// that matters is the WORST SINGLE FILE, and it is the one asserted here.
///
/// This is the asserting half of a pair. <c>CorpusPerfTests.WorkspaceLints_WhereTheTimeGoes</c>
/// reports the same quantities in detail and asserts nothing; this reports the summary and fails.
/// It lives in the Corpus category rather than beside that sweep for the reason that sweep's own
/// header gives — the budget gates belong here.
///
/// It measures through <see cref="LintTimings"/> rather than <c>PerfTracker</c>, deliberately.
/// PerfTracker's every method is <c>[Conditional("GSCODE_INSTRUMENTATION")]</c>, so a gate built on
/// it would collect nothing in the ordinary Release build this category runs in, assert over an
/// empty set, and pass by measuring nothing — the failure mode that makes a green corpus run
/// meaningless. The per-file report below is the proof it really measured.
/// </summary>
[Trait("Category", "Corpus")]
[Collection(GameProfileCollection.Name)]
public class LintBudgetTests
{
    /// <summary>
    /// The share of the debounce ONE rule may spend on its worst file. Over this, the test fails.
    ///
    /// Set from measurement rather than taste, and read with <see cref="PerRuleWatchShareOfDebounce"/>
    /// below — see PERF.md's per-lint budget section for the runs. The worst scope measured on either
    /// dialect was <c>lint.NodeLintPass</c> at 9.2–14.4% of the debounce across three runs of one
    /// build, so this sits about 2.8x over the worst reading and about 4x over the typical one. That
    /// scope then included the flow typer's inference walk, which is now
    /// <c>lint.FlowTyper.InferValues</c> at about 8% on its own.
    ///
    /// Generous ON PURPOSE. The same three runs put that rule's own worst file anywhere in a 23–36 ms
    /// band with nothing changed, so a bound near the measurement would fail on the machine rather
    /// than on the code — and a flaky gate gets deleted, after which nothing is bounded at all. This
    /// is sized to catch a rule that has become several times more expensive, which is the shape
    /// every regression in this pass's recorded history has actually had.
    /// </summary>
    private const double PerRuleShareOfDebounce = 0.40;

    /// <summary>
    /// The share at which a rule is CALLED OUT in the report without failing the run.
    ///
    /// The fail bound above is deliberately loose, which leaves a gap: a rule could double and still
    /// pass silently. This is the line the measured worst case would have to grow about 1.4x to
    /// cross, so it turns that gap into something a reader sees the next time the corpus is swept.
    /// </summary>
    private const double PerRuleWatchShareOfDebounce = 0.20;

    /// <summary>
    /// The share the WHOLE pass may spend at its 99th-percentile file. A looser bound on a bigger
    /// quantity, and it catches what the per-rule bound cannot: twenty rules each growing by half,
    /// where no single one crosses its own budget. Measured at 24–31 ms on bo3 and 12 ms on cod4,
    /// so this is about 2x over the worst of those.
    ///
    /// p99 rather than max, because one pathological file in a corpus of thousands is a fact about
    /// that file — bo3's whole-pass max alone ranges 61–85 ms run to run. The per-rule bound above
    /// is where the worst case is read; <c>TextSyncHandler</c> is what catches a whole analysis
    /// crossing the debounce on a real workspace.
    /// </summary>
    private const double WholePassP99ShareOfDebounce = 0.25;

    private static double PerRuleBudgetMilliseconds
    {
        get { return AnalysisTiming.DebounceMilliseconds * PerRuleShareOfDebounce; }
    }

    private static double WholePassP99BudgetMilliseconds
    {
        get { return AnalysisTiming.DebounceMilliseconds * WholePassP99ShareOfDebounce; }
    }

    private readonly ITestOutputHelper _output;

    public LintBudgetTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// BO3 and CoD4, because the two dialects run different rules. <c>UnusedIncludeLint</c> and
    /// <c>IncludeUsageLint</c> walk an include closure on the merge dialect and stand down entirely
    /// on the namespace one, so a BO3-only gate would leave them unbudgeted — they are among the
    /// most expensive rules that exist, on the corpus where they run.
    /// </summary>
    [Fact]
    public async Task EveryLint_StaysWellInsideTheKeystrokeDebounce()
    {
        List<string> violations = [];
        bool measured = false;

        if ( CorpusFixture.Available )
        {
            violations.AddRange(await MeasureAsync(
                GameProfile.BlackOps3,
                CorpusFixture.RawRoot!,
                CorpusFixture.Resolver,
                CorpusFixture.Scripts,
                (path, resolver, names) => CorpusFixture.Analyze(path, resolver, names)));

            measured = true;
        }

        GameCorpus? cod4 = GameCorpusFixture.For(GameProfile.Cod4);
        if ( cod4 is not null )
        {
            GameCorpus captured = cod4;
            violations.AddRange(await MeasureAsync(
                captured.Profile,
                captured.RawRoot,
                () => GameCorpusFixture.Resolver(captured),
                () => GameCorpusFixture.Scripts(captured),
                (path, resolver, names) => GameCorpusFixture.Analyze(captured, path, resolver, names)));

            measured = true;
        }

        if ( !measured )
        {
            _output.WriteLine("SKIPPED: neither %GSCODE_CORPUS_BO3% nor %GSCODE_CORPUS_COD4% found.");
            return;
        }

        // Assert.Empty names nothing it found, and a budget failure that says only "collection was
        // not empty" sends the reader back for another corpus run to learn which rule and which
        // file. The violations carry both, so they are the message.
        Assert.True(
            violations.Count == 0,
            "A lint rule is over its share of the keystroke debounce:" + Environment.NewLine
                + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// Times one game's whole corpus and returns the budget violations, having reported every rule.
    ///
    /// Needs a FINISHED index, like the perf sweep next door: two of the heaviest rules stand down
    /// without one, so a partial index would budget the cheap half and call it the total.
    /// </summary>
    private async Task<IReadOnlyList<string>> MeasureAsync(
        GameProfile profile,
        string corpusRoot,
        Func<PathResolver> resolverFactory,
        Func<IReadOnlyList<string>> scriptsFactory,
        Func<string, PathResolver, NameTable, ParseResult> analyse)
    {
        // GameProfile.Active is process-global — the indexer enumerates through its script globs and
        // several lints fall back to it. GameProfileCollection is what stops this racing another
        // class; restoring it is what stops it leaking into the next test in this one.
        GameProfile previous = GameProfile.Active;
        try
        {
            GameProfile.Select(profile.ShortName);

            PathResolver resolver = resolverFactory();
            NameTable names = new();
            ScriptDatabase database = new();

            WorkspaceIndexer indexer = new(database, () => resolver, new PhysicalFileSystem(), names);
            await indexer.IndexAsync(IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None);

            string apiDirectory = Path.Combine(AppContext.BaseDirectory, "Api");
            BuiltinApiSet builtins = BuiltinApiSet.Load(apiDirectory);
            ObjectFields objectFields = ObjectFields.Load(apiDirectory);

            Dictionary<string, RuleCost> perRule = new(StringComparer.Ordinal);
            List<LintFileCost> files = [];
            LintTimings timings = new();

            foreach ( string path in scriptsFactory() )
            {
                ScriptLanguage language = ScriptAnalysis.LanguageFromPath(path);

                ParseResult parsed;
                try
                {
                    parsed = analyse(path, resolver, names);

                    // Warm, then measure. A file that throws is the parse gate's business, not this
                    // one's, so it is dropped rather than counted as fast.
                    WorkspaceLints.LintsOnly(parsed, language, path, database, resolver, builtins, objectFields);
                }
                catch ( Exception )
                {
                    continue;
                }

                timings.Clear();

                Stopwatch watch = Stopwatch.StartNew();
                WorkspaceLints.LintsOnly(
                    parsed, language, path, database, resolver, builtins, objectFields,
                    cancellationToken: CancellationToken.None, timings: timings);
                watch.Stop();

                List<(string Name, double Milliseconds)> rules = [];
                foreach ( KeyValuePair<string, double> scope in timings.Milliseconds )
                {
                    perRule.TryGetValue(scope.Key, out RuleCost running);
                    perRule[scope.Key] = running.With(scope.Value, path);
                    rules.Add((scope.Key, scope.Value));
                }

                // Every file, not just the slow ones: the page ranks them, and a threshold here
                // would decide in advance which files are allowed to be interesting.
                files.Add(new LintFileCost(path, watch.Elapsed.TotalMilliseconds, rules));
            }

            return Report(profile.ShortName, corpusRoot, perRule, files);
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
        }
    }

    /// <summary>
    /// One rule's cost across a corpus: enough to name the worst file, which is what makes a
    /// failure actionable without a second run.
    /// </summary>
    private readonly record struct RuleCost(double Total, double Max, string WorstPath, int Files)
    {
        public RuleCost With(double milliseconds, string path)
        {
            return milliseconds > Max
                ? new RuleCost(Total + milliseconds, milliseconds, path, Files + 1)
                : new RuleCost(Total + milliseconds, Max, WorstPath, Files + 1);
        }
    }

    private IReadOnlyList<string> Report(
        string game,
        string corpusRoot,
        IReadOnlyDictionary<string, RuleCost> perRule,
        List<LintFileCost> files)
    {
        List<string> violations = [];

        _output.WriteLine("");
        _output.WriteLine($"{game}: {files.Count} files, per-rule budget "
            + $"{PerRuleBudgetMilliseconds:F1} ms ({PerRuleShareOfDebounce:P0} of the "
            + $"{AnalysisTiming.DebounceMilliseconds} ms debounce), watch line at "
            + $"{PerRuleWatchShareOfDebounce:P0}");

        if ( files.Count == 0 )
        {
            _output.WriteLine("    no files linted — nothing was measured");
            return violations;
        }

        List<LintRuleCost> ranked = [.. perRule
            .Select(static entry => new LintRuleCost(
                entry.Key, entry.Value.Max, entry.Value.WorstPath, entry.Value.Total, entry.Value.Files))
            .OrderByDescending(static rule => rule.Max)];

        foreach ( LintRuleCost rule in ranked )
        {
            double share = rule.Max / AnalysisTiming.DebounceMilliseconds;
            string watch = share > PerRuleWatchShareOfDebounce ? "  <- WATCH" : "";

            _output.WriteLine(
                $"    {rule.Name,-45} max {rule.Max,7:F2} ms ({share * 100,5:F1}% of debounce)  "
                + $"total {rule.Total,8:F0} ms over {rule.Files,5:N0} files{watch}");

            if ( rule.Max > PerRuleBudgetMilliseconds )
            {
                violations.Add(
                    $"{game}: {rule.Name} took {rule.Max:F1} ms on one file, over the "
                    + $"{PerRuleBudgetMilliseconds:F1} ms budget ({PerRuleShareOfDebounce:P0} of the "
                    + $"{AnalysisTiming.DebounceMilliseconds} ms debounce) — {rule.WorstPath}");
            }
        }

        List<double> sorted = [.. files.Select(static file => file.Milliseconds).Order()];
        double p99 = sorted[(int)Math.Clamp(Math.Round(0.99 * (sorted.Count - 1)), 0, sorted.Count - 1)];

        _output.WriteLine(
            $"    whole pass: median {sorted[sorted.Count / 2]:F2} ms | p99 {p99:F2} ms | max {sorted[^1]:F2} ms "
            + $"| budget p99 {WholePassP99BudgetMilliseconds:F1} ms");

        if ( p99 > WholePassP99BudgetMilliseconds )
        {
            violations.Add(
                $"{game}: the whole lint pass reached {p99:F1} ms at p99, over the "
                + $"{WholePassP99BudgetMilliseconds:F1} ms budget — no single rule need be at fault, "
                + "so read the per-rule table above for where it went.");
        }

        WriteReport(game, corpusRoot, ranked, files, sorted, p99);

        return violations;
    }

    /// <summary>
    /// The page, beside the perf sweep's and under its own name.
    ///
    /// The console table above is a summary and scrolls past; this holds every rule's worst file and
    /// the slowest files' own breakdown, which is what turns "a rule took 23 ms" into a file someone
    /// can open. <c>GSCODE_PERF_REPORT</c> overrides the directory, matching the sweeps.
    /// </summary>
    private void WriteReport(
        string game,
        string corpusRoot,
        IReadOnlyList<LintRuleCost> rules,
        List<LintFileCost> files,
        List<double> sorted,
        double p99)
    {
        string directory = ReportPage.OutputDirectory("GSCODE_PERF_REPORT");
        string path = Path.Combine(directory, ReportPage.BudgetPage(game));

        PerfReport.WriteLintBudget(path, new LintBudgetReport(
            game,
            corpusRoot,
            AnalysisTiming.DebounceMilliseconds,
            PerRuleBudgetMilliseconds,
            AnalysisTiming.DebounceMilliseconds * PerRuleWatchShareOfDebounce,
            files.Count,
            sorted[sorted.Count / 2],
            p99,
            sorted[^1],
            rules,
            [.. files.OrderByDescending(static file => file.Milliseconds).Take(25)]));

        _output.WriteLine($"    report: {path}");
    }
}
