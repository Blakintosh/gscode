using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GSCode.Server.Handlers;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// What a perf sweep timed. It decides what one row is — a file or a request — and which of the
/// page's sections mean anything: a lint pass has no lex/parse split, and a completion's cost follows
/// the workspace rather than the file, so its time per kilobyte is noise.
/// </summary>
internal enum PerfSweep
{
    /// <summary>Lex, preprocess, parse and extract, one row per file.</summary>
    Analysis,

    /// <summary>The cross-file lint pass over a finished index, one row per file.</summary>
    Lints,

    /// <summary>Completion requests over a finished index, one row per REQUEST.</summary>
    Completion,
}

/// <summary>
/// One sweep's run, as written to the JSON sidecar and read back by the aggregate. <see cref="Game"/>
/// is the sidecar name, so it carries the sweep as a suffix (<c>bo3-lints</c>); the aggregate splits
/// it back apart rather than storing a second field that could disagree with the file name.
/// </summary>
internal sealed record GameSummary(
    string Game,
    string Root,
    string GeneratedAt,
    int Files,
    double TotalMilliseconds,
    double Median,
    double P90,
    double P99,
    double Max,
    double Lex,
    double Preprocess,
    double Parse,
    double Extract,
    List<SubPhaseRow> SubPhases,
    List<FileRow> TopFiles,
    EntryCounts? Entries = null);

/// <summary>
/// One scope's cost across a run — the corpus-wide sum AND the distribution over the files it ran
/// in.
///
/// The sum is what a phase table is read against; the distribution is what a DEBOUNCE is read
/// against. A rule reading 144 ms over 980 files says nothing about what one keystroke pays, and
/// the keystroke is the only thing a user waits for, so <see cref="Max"/> — the worst single file —
/// is the number the budget gate in <c>CorpusTests</c> asserts on.
///
/// <see cref="Files"/> counts the files the scope actually ran in, which is not the corpus size: a
/// rule that stands down on a dialect or behind an unfinished index is ABSENT from those files
/// rather than zero in them, and counting the absences would halve every statistic here.
/// </summary>
internal sealed record SubPhaseRow(
    string Name,
    double Milliseconds,
    long Count,
    int Files,
    double Median,
    double P90,
    double P99,
    double Max);

internal sealed record FileRow(string Path, double Milliseconds, long Bytes);

/// <summary>
/// One sweep's every row, for reading outside the page: the sidecar keeps only what the hub needs
/// (totals, percentiles, the slowest 50), and a question nobody built a table for needs the rest.
/// <see cref="Rows"/> is one per file, or one per REQUEST on a completion sweep.
/// </summary>
internal sealed record PerfDetail(
    string Sweep,
    string Name,
    string Root,
    string GeneratedAt,
    List<PerfDetailRow> Rows);

/// <summary>
/// One timed row. The four phases are only measured by the analysis sweep and are null elsewhere,
/// rather than zero, so a lint row does not claim it lexed in no time. <see cref="Scopes"/> is the
/// row's named scopes - per rule on a lint sweep - and is null when nothing was recorded.
/// </summary>
internal sealed record PerfDetailRow(
    string Path,
    long Bytes,
    double Milliseconds,
    double? Lex,
    double? Preprocess,
    double? Parse,
    double? Extract,
    SortedDictionary<string, PerfDetailScope>? Scopes);

internal sealed record PerfDetailScope(double Milliseconds, long Count);

/// <summary>
/// How big a completion sweep's returned lists were. Only the statement-scope arm reaches the store
/// queries and returns thousands of entries, so this is what says whether the timings beside it
/// measured the expensive path or a sample that landed on cheap arms.
/// </summary>
internal sealed record EntryCounts(int Requests, double Median, double P90, int Max, int OverFiveHundred);

/// <summary>One rule's cost over a corpus, keeping the file that produced its worst reading.</summary>
internal sealed record LintRuleCost(string Name, double Max, string WorstPath, double Total, int Files);

/// <summary>One file's whole lint pass, split by rule.</summary>
internal sealed record LintFileCost(
    string Path,
    double Milliseconds,
    IReadOnlyList<(string Name, double Milliseconds)> Rules);

/// <summary>Everything the lint budget page shows, gathered by the gate that asserts on it.</summary>
internal sealed record LintBudgetReport(
    string Game,
    string CorpusRoot,
    double DebounceMilliseconds,
    double PerRuleBudgetMilliseconds,
    double WatchMilliseconds,
    int FileCount,
    double Median,
    double P99,
    double Max,
    IReadOnlyList<LintRuleCost> Rules,
    IReadOnlyList<LintFileCost> SlowestFiles);

/// <summary>
/// The perf sweeps as standalone HTML pages, written beside the diagnostic sweep but never by it: a
/// perf run costs a second pass over every script, so it is opted into rather than carried along.
///
/// Two tables, because they answer different questions. Slowest ABSOLUTE says where the wall-clock
/// went. Slowest PER KILOBYTE says where an algorithm is superlinear — a long file taking long is
/// arithmetic, a short file taking long is a bug.
/// </summary>
internal static class PerfReport
{
    private const string AnalysisKind = "analysis";

    /// <summary>
    /// The upper edges of the distribution bands, in ms. Roughly x3 apart, so a median near 0.1 ms and
    /// a tail near 50 ms both land in bands of their own.
    /// </summary>
    private static readonly double[] s_bandEdges = [0.1, 0.3, 1, 3, 10, 30, 100];

    /// <summary>
    /// <paramref name="SubPhases"/> is this file's <c>PerfTracker</c> scopes, and is EMPTY unless
    /// the build defined <c>GSCODE_INSTRUMENTATION</c> — both the scope calls and the snapshot that
    /// reads them are <c>[Conditional]</c>, so an ordinary build never populates it. Empty is the
    /// normal case, not a failure, and the report says so rather than showing a blank table.
    /// </summary>
    internal sealed record Item(
        string Path, double Milliseconds, long Bytes,
        double Lex = 0, double Preprocess = 0, double Parse = 0, double Extract = 0,
        IReadOnlyDictionary<string, (double Milliseconds, long Count)>? SubPhases = null)
    {
        public double MillisecondsPerKilobyte
        {
            get { return Bytes == 0 ? 0 : Milliseconds / (Bytes / 1024.0); }
        }
    }

    /// <summary>
    /// One file's completion requests folded into a row. The distribution is read over requests, but
    /// a table of them lists every file up to eleven times, and "which file" is the question a row
    /// is there to answer.
    /// </summary>
    private sealed record FileGroup(string Path, long Bytes, int Requests, double Median, double Max, double Total);

    /// <summary>
    /// Each named scope's corpus-wide sum and its per-file distribution, ordered by total time.
    /// Returns empty when nothing was instrumented.
    ///
    /// The per-file samples come from <see cref="Item.SubPhases"/>, which the sweep fills by
    /// resetting the tracker before each file and snapshotting after it — so one entry is one
    /// file's cost for that scope, and a percentile over them is a percentile over FILES.
    /// </summary>
    public static IReadOnlyList<SubPhaseRow> SubPhaseStats(IReadOnlyList<Item> items)
    {
        Dictionary<string, (double Milliseconds, long Count)> totals = new(StringComparer.Ordinal);
        Dictionary<string, List<double>> perFile = new(StringComparer.Ordinal);

        foreach ( Item item in items )
        {
            if ( item.SubPhases is null )
            {
                continue;
            }

            foreach ( KeyValuePair<string, (double Milliseconds, long Count)> scope in item.SubPhases )
            {
                totals.TryGetValue(scope.Key, out (double Milliseconds, long Count) running);
                totals[scope.Key] = (running.Milliseconds + scope.Value.Milliseconds, running.Count + scope.Value.Count);

                if ( !perFile.TryGetValue(scope.Key, out List<double>? samples) )
                {
                    samples = [];
                    perFile[scope.Key] = samples;
                }

                samples.Add(scope.Value.Milliseconds);
            }
        }

        List<SubPhaseRow> rows = [];
        foreach ( KeyValuePair<string, (double Milliseconds, long Count)> total in totals )
        {
            List<double> sorted = [.. perFile[total.Key].Order()];

            rows.Add(new SubPhaseRow(
                total.Key,
                total.Value.Milliseconds,
                total.Value.Count,
                sorted.Count,
                Percentile(sorted, 0.50),
                Percentile(sorted, 0.90),
                Percentile(sorted, 0.99),
                sorted.Count > 0 ? sorted[^1] : 0));
        }

        return [.. rows.OrderByDescending(static row => row.Milliseconds)];
    }

    /// <summary>The entry-count distribution of a completion sweep, or null when there is none.</summary>
    public static EntryCounts? SummarizeEntries(IReadOnlyList<int>? counts)
    {
        if ( counts is null || counts.Count == 0 )
        {
            return null;
        }

        List<double> sorted = [.. counts.Select(static count => (double)count).Order()];
        int overFiveHundred = counts.Count(static count => count > 500);

        return new EntryCounts(
            counts.Count,
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.90),
            (int)sorted[^1],
            overFiveHundred);
    }

    /// <summary>The sidecar and page name: the game alone for the parse sweep, suffixed for the rest.</summary>
    public static string SidecarName(string game, PerfSweep sweep)
    {
        string kind = KindOf(sweep);
        if ( kind == AnalysisKind )
        {
            return game;
        }

        return game + "-" + kind;
    }

    private static string KindOf(PerfSweep sweep)
    {
        switch ( sweep )
        {
            case PerfSweep.Lints:
                return "lints";
            case PerfSweep.Completion:
                return "completion";
            default:
                return AnalysisKind;
        }
    }

    /// <summary>
    /// The game half of a sidecar name. Read from the NAME so sidecars written before the sweep kind
    /// was tracked — and those of the retired codelens sweep — still group with their game.
    /// </summary>
    private static string BaseGameOf(string name)
    {
        int dash = name.IndexOf('-', StringComparison.Ordinal);
        if ( dash < 0 )
        {
            return name;
        }

        return name[..dash];
    }

    /// <summary>The sweep half of a sidecar name, <c>analysis</c> when there is none.</summary>
    private static string KindOf(string name)
    {
        int dash = name.IndexOf('-', StringComparison.Ordinal);
        if ( dash < 0 )
        {
            return AnalysisKind;
        }

        return name[(dash + 1)..];
    }

    private static string KindHeading(string kind)
    {
        switch ( kind )
        {
            case AnalysisKind:
                return "Analysis timing";
            case "lints":
                return "Cross-file lint timing";
            case "completion":
                return "Completion timing";
            default:
                return $"{kind} timing";
        }
    }

    /// <summary>What one row of a sweep counts: a completion sweep times requests, the rest files.</summary>
    private static string UnitOf(string kind)
    {
        return kind == "completion" ? "requests" : "files";
    }

    /// <summary>A snapshot of the pools that actually decide the server's footprint.</summary>
    internal sealed record Memory(long ManagedLive, long HeapSize, long Committed, long Fragmented, long WorkingSet, int Gen0, int Gen1, int Gen2);

    /// <summary>
    /// Taken AFTER a forced collection, so it reports what is retained rather than what happens to
    /// be uncollected. Fragmented is the gap between the heap and what is live in it, which on an
    /// indexing run is mostly large-object heap and is the number that has misled before: a large
    /// working set with a small live graph is holes, not a leak.
    /// </summary>
    public static Memory Sample()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        GCMemoryInfo info = GC.GetGCMemoryInfo();
        long live = GC.GetTotalMemory(forceFullCollection: false);

        return new Memory(
            ManagedLive: live,
            HeapSize: info.HeapSizeBytes,
            Committed: info.TotalCommittedBytes,
            Fragmented: info.FragmentedBytes,
            WorkingSet: Environment.WorkingSet,
            Gen0: GC.CollectionCount(0),
            Gen1: GC.CollectionCount(1),
            Gen2: GC.CollectionCount(2));
    }

    /// <summary>
    /// One sweep's page, its JSON sidecar, and a rebuilt aggregate. The page is laid out answer
    /// first: the headline numbers and the distribution, then where the time went, then the rows,
    /// and the run's context (worlds, memory) last.
    /// </summary>
    public static void Write(
        string outputPath, string game, PerfSweep sweep, IReadOnlyList<Item> items, string corpusRoot,
        IReadOnlyDictionary<string, int> worldCounts, Memory memory, int cachedHeaders,
        IReadOnlyList<int>? entryCounts)
    {
        string directory = Path.GetDirectoryName(outputPath)!;
        string kind = KindOf(sweep);
        string unit = UnitOf(kind);
        string generatedAt = DateTime.Now.ToString("s", CultureInfo.InvariantCulture);
        EntryCounts? entries = SummarizeEntries(entryCounts);

        List<double> sorted = [.. items.Select(static i => i.Milliseconds).Order()];
        double total = sorted.Sum();
        int tailCount = (items.Count / 100) + 1;
        double tail = items.OrderByDescending(static i => i.Milliseconds).Take(tailCount).Sum(static i => i.Milliseconds);
        IReadOnlyList<SubPhaseRow> subPhases = SubPhaseStats(items);

        StringBuilder html = new();
        ReportPage.Head(html, $"GSCode perf - {SidecarName(game, sweep)}");
        ReportPage.GameNav(html, directory, game, KindOf(sweep));

        html.AppendLine($"<h1>{ReportPage.Escape(KindHeading(kind))} - {ReportPage.Escape(game)}</h1>");
        html.AppendLine($"<div class=\"sub\">{ReportPage.Escape(Describe(sweep, items))} From "
            + $"<code>{ReportPage.Escape(corpusRoot)}</code>, run at {ReportPage.Escape(generatedAt)}.</div>");

        html.AppendLine("<div class=\"stats\">");
        ReportPage.Stat(html, unit, $"{items.Count:N0}");
        ReportPage.Stat(html, $"total, all {unit}", $"{total:F0} ms");
        ReportPage.Stat(html, "median", $"{Percentile(sorted, 0.50):F2} ms");
        ReportPage.Stat(html, "p90", $"{Percentile(sorted, 0.90):F2} ms");
        ReportPage.Stat(html, "p99", $"{Percentile(sorted, 0.99):F2} ms");
        ReportPage.Stat(html, "max", $"{(sorted.Count > 0 ? sorted[^1] : 0):F2} ms");
        ReportPage.Stat(html, $"slowest {tailCount} {unit}", total > 0 ? $"{tail / total * 100:F1}% of total" : "-");
        html.AppendLine("</div>");

        if ( entries is not null )
        {
            EntryCountsSection(html, entries);
        }

        Distribution(html, sorted, unit);

        if ( sweep == PerfSweep.Analysis )
        {
            PhaseTable(html, items);
        }

        SubPhaseTable(html, sweep, subPhases);

        if ( sweep == PerfSweep.Completion )
        {
            List<FileGroup> groups = GroupByFile(items);

            html.AppendLine("<h2>Slowest files, by their worst request</h2>");
            html.AppendLine("<div class=\"sub\">Each file's requests folded into one row. Time per "
                + "kilobyte is left out on purpose: a completion walks the record store, so it costs "
                + "what the WORKSPACE costs, and a one-line file is as expensive as a long one.</div>");
            GroupTable(html, null, [.. groups.OrderByDescending(static g => g.Max).Take(25)], corpusRoot);

            html.AppendLine("<h2>All files</h2>");
            html.AppendLine($"<div class=\"sub\">Every one of the {groups.Count:N0} files sampled. Click "
                + "a header to sort; type to filter by path.</div>");
            html.AppendLine("<input class=\"q\" data-filter=\"all\" placeholder=\"filter by path...\">");
            GroupTable(html, "all", [.. groups.OrderByDescending(static g => g.Max)], corpusRoot, legend: false);
        }
        else
        {
            bool phases = sweep == PerfSweep.Analysis;

            html.AppendLine("<h2>Slowest by absolute time</h2>");
            html.AppendLine("<div class=\"sub\">Where the wall-clock went.</div>");
            ItemTable(html, null, [.. items.OrderByDescending(static i => i.Milliseconds).Take(25)], corpusRoot, phases);

            html.AppendLine("<h2>Slowest per kilobyte</h2>");
            html.AppendLine("<div class=\"sub\">Files over 4 KB, ranked by time per KB. A short file high "
                + "on this list is where to look for superlinear behaviour - a long one is just long.</div>");
            ItemTable(html, null,
                [.. items.Where(static i => i.Bytes > 4096).OrderByDescending(static i => i.MillisecondsPerKilobyte).Take(25)],
                corpusRoot, phases, legend: false);

            // The two tables above are the curated answer to "what is slow"; this is the DATA, because
            // a top-25 discards 97% of a run and no question outside the ones already asked can be
            // answered from it.
            html.AppendLine("<h2>All files</h2>");
            html.AppendLine($"<div class=\"sub\">Every one of the {items.Count:N0} files timed. Click a "
                + "header to sort; type to filter by path.</div>");
            html.AppendLine("<input class=\"q\" data-filter=\"all\" placeholder=\"filter by path...\">");
            ItemTable(html, "all", [.. items.OrderByDescending(static i => i.Milliseconds)], corpusRoot, phases, legend: false);
        }

        RunContext(html, sweep, worldCounts, memory, cachedHeaders);
        ReportPage.Foot(html);

        ReportPage.Save(outputPath, html);

        WriteSummary(directory, SidecarName(game, sweep), generatedAt, items, corpusRoot, sorted, subPhases, entries);
        WriteDetail(directory, sweep, SidecarName(game, sweep), generatedAt, items, corpusRoot);
        WriteAggregate(directory);
    }

    private static readonly JsonSerializerOptions s_detailJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Every row the page's "All files" table holds, with each row's scopes, as JSON beside the page.
    /// Slowest first, which is the order the page lists them in.
    /// </summary>
    private static void WriteDetail(
        string directory, PerfSweep sweep, string name, string generatedAt, IReadOnlyList<Item> items, string root)
    {
        bool phases = sweep == PerfSweep.Analysis;
        List<PerfDetailRow> rows = new(items.Count);

        foreach ( Item item in items.OrderByDescending(static i => i.Milliseconds) )
        {
            SortedDictionary<string, PerfDetailScope>? scopes = null;
            if ( item.SubPhases is not null && item.SubPhases.Count > 0 )
            {
                scopes = new SortedDictionary<string, PerfDetailScope>(StringComparer.Ordinal);
                foreach ( KeyValuePair<string, (double Milliseconds, long Count)> scope in item.SubPhases )
                {
                    scopes[scope.Key] = new PerfDetailScope(scope.Value.Milliseconds, scope.Value.Count);
                }
            }

            rows.Add(new PerfDetailRow(
                ReportPage.Relative(item.Path, root),
                item.Bytes,
                item.Milliseconds,
                phases ? item.Lex : null,
                phases ? item.Preprocess : null,
                phases ? item.Parse : null,
                phases ? item.Extract : null,
                scopes));
        }

        PerfDetail detail = new(KindOf(sweep), name, root, generatedAt, rows);
        string path = Path.Combine(directory, ReportPage.DetailFile(name));
        File.WriteAllText(path, JsonSerializer.Serialize(detail, s_detailJson));
    }

    private static string Describe(PerfSweep sweep, IReadOnlyList<Item> items)
    {
        switch ( sweep )
        {
            case PerfSweep.Lints:
                return $"{items.Count:N0} files, each linted once to warm and then timed against a "
                    + "finished index. The parse is done before the stopwatch, so this is lint cost only.";
            case PerfSweep.Completion:
                int files = items.Select(static i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                return $"{items.Count:N0} completion requests across {files:N0} files - one at file "
                    + "scope and up to ten at call sites per file, each warmed and then timed against a "
                    + "finished index. The headline numbers are per REQUEST; the tables fold them per file.";
            default:
                return $"{items.Count:N0} files, each lexed, preprocessed, parsed and extracted once to "
                    + "warm, then timed.";
        }
    }

    /// <summary>
    /// The entry counts beside the completion timings. A fast median means nothing about the
    /// store queries unless the median request reached them, and only the requests returning
    /// hundreds of entries did.
    /// </summary>
    private static void EntryCountsSection(StringBuilder html, EntryCounts entries)
    {
        double share = entries.Requests == 0 ? 0 : entries.OverFiveHundred * 100.0 / entries.Requests;

        html.AppendLine("<h2>What the requests returned</h2>");
        html.AppendLine("<div class=\"sub\">Entries per completion list. Only the statement-scope arm "
            + "queries the store and returns hundreds of entries; if few requests did, the timings above "
            + "measured the cheap arms and say little about the expensive one.</div>");
        html.AppendLine("<div class=\"stats\">");
        ReportPage.Stat(html, "median entries", $"{entries.Median:F0}");
        ReportPage.Stat(html, "p90 entries", $"{entries.P90:F0}");
        ReportPage.Stat(html, "max entries", $"{entries.Max:N0}");
        ReportPage.Stat(html, "requests over 500 entries", $"{entries.OverFiveHundred:N0} ({share:F1}%)");
        html.AppendLine("</div>");
    }

    /// <summary>
    /// Rows and time per band. The share of TIME beside the share of rows is what says whether the
    /// tail is worth chasing: 1% of files carrying 30% of the time is, 1% carrying 2% is not.
    /// </summary>
    private static void Distribution(StringBuilder html, List<double> sorted, string unit)
    {
        double total = sorted.Sum();

        html.AppendLine("<h2>Distribution</h2>");
        html.AppendLine($"<div class=\"sub\">How many {ReportPage.Escape(unit)} fall in each band, and how much of "
            + "the total time they carry.</div>");
        string one = unit == "requests" ? "request" : "file";
        ReportPage.TableStart(html, null,
        [
            new Column("band", $"a range of time one {one} took"),
            new Column(unit, $"how many {unit} took a time in that range"),
            new Column($"share of {unit}", $"those {unit} as a share of all of them"),
            new Column("share of time", "their time added up, as a share of the whole sweep's time"),
        ]);

        int index = 0;
        double lower = 0;
        for ( int band = 0; band <= s_bandEdges.Length; band++ )
        {
            double upper = band < s_bandEdges.Length ? s_bandEdges[band] : double.PositiveInfinity;
            int count = 0;
            double time = 0;

            while ( index < sorted.Count && sorted[index] < upper )
            {
                count++;
                time += sorted[index];
                index++;
            }

            string label;
            if ( band == 0 )
            {
                label = $"< {Band(upper)} ms";
            }
            else if ( band == s_bandEdges.Length )
            {
                label = $">= {Band(lower)} ms";
            }
            else
            {
                label = $"{Band(lower)} - {Band(upper)} ms";
            }

            double rowShare = sorted.Count == 0 ? 0 : count * 100.0 / sorted.Count;
            double timeShare = total <= 0 ? 0 : time * 100 / total;

            html.AppendLine($"<tr><td>{ReportPage.Escape(label)}</td><td class=\"n\">{count}</td>"
                + $"{ReportPage.BarCell(rowShare)}{ReportPage.BarCell(timeShare)}</tr>");

            lower = upper;
        }

        ReportPage.TableEnd(html);
    }

    private static string Band(double edge)
    {
        return edge.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Which PHASE the time went to, across the whole corpus. Only two of the four are per-function
    /// work, so this is the level at which "why is this file slow" has an answer: preprocess points
    /// at what it inserts, parse at its size and shape, extract at how much it declares.
    /// </summary>
    private static void PhaseTable(StringBuilder html, IReadOnlyList<Item> items)
    {
        double lex = items.Sum(static i => i.Lex);
        double pre = items.Sum(static i => i.Preprocess);
        double par = items.Sum(static i => i.Parse);
        double ext = items.Sum(static i => i.Extract);
        double phases = lex + pre + par + ext;

        html.AppendLine("<h2>Where the time goes, by phase</h2>");
        ReportPage.TableStart(html, null,
        [
            new Column("phase", "one stage of analysing a file: lex, preprocess, parse, extract"),
            new Column("ms", "time in that stage, added up over every file"),
            new Column("share", "that stage's share of the four stages' time"),
        ]);
        PhaseRow(html, "lex", lex, phases);
        PhaseRow(html, "preprocess", pre, phases);
        PhaseRow(html, "parse", par, phases);
        PhaseRow(html, "extract", ext, phases);
        ReportPage.TableEnd(html);
    }

    private static void PhaseRow(StringBuilder html, string label, double ms, double total)
    {
        double share = total > 0 ? ms / total * 100 : 0;
        html.AppendLine($"<tr><td>{ReportPage.Escape(label)}</td><td class=\"n\">{ms:F0}</td>{ReportPage.BarCell(share)}</tr>");
    }

    /// <summary>
    /// The instrumented scopes — or, on the lint page, the rules, which <c>LintTimings</c> records in
    /// an ordinary build. There it is the page's main answer, so it is titled as one.
    /// </summary>
    private static void SubPhaseTable(StringBuilder html, PerfSweep sweep, IReadOnlyList<SubPhaseRow> subPhases)
    {
        bool lints = sweep == PerfSweep.Lints;
        html.AppendLine(lints ? "<h2>Where the time goes, by rule</h2>" : "<h2>Sub-phases</h2>");

        if ( subPhases.Count == 0 )
        {
            html.AppendLine("<div class=\"sub\">Not instrumented. Rebuild with "
                + "<code>-p:GscodeInstrumentation=true</code> to record these — the scope calls are "
                + "<code>[Conditional]</code>, so an ordinary build carries none of them.</div>");
            return;
        }

        string intro = lints
            ? "One row per rule, from <code>LintTimings</code>, plus any instrumented scopes. "
                + "<code>total ms</code> is a corpus total; the per-file columns are what one keystroke pays."
            : "Nested inside the phases above, so they do not sum to the total.";
        html.AppendLine($"<div class=\"sub\">{intro}</div>");

        ReportPage.TableStart(html, null,
        [
            lints
                ? new Column("rule", "one lint rule. lint.FlowTyper.InferValues is the type inference the type-aware "
                    + "rules share, timed on its own")
                : new Column("scope", "one measured step inside a phase"),
            new Column("total ms", "its time added up over every file in the sweep: a corpus total, not one file's cost"),
            new Column("calls", "how many times it ran, over all files"),
            new Column("mean ms", "total ms divided by calls"),
            new Column("files", "how many files it ran in"),
            new Column("median ms", "its time in one file: half the files took less"),
            new Column("p90 ms", "its time in one file: 90% of the files took less"),
            new Column("p99 ms", "its time in one file: 99% of the files took less"),
            new Column("max ms", "its time in the single slowest file. " + ReportPage.OneReadingNote),
            new Column("max % debounce", $"max ms against the {AnalysisTiming.DebounceMilliseconds} ms keystroke debounce, "
                + "the only budget an interactive path has"),
        ]);

        foreach ( SubPhaseRow scope in subPhases )
        {
            double mean = scope.Count == 0 ? 0 : scope.Milliseconds / scope.Count;
            double debounce = scope.Max / AnalysisTiming.DebounceMilliseconds * 100;

            html.AppendLine($"<tr><td><code>{ReportPage.Escape(scope.Name)}</code></td>"
                + $"<td class=\"n\">{scope.Milliseconds:F0}</td><td class=\"n\">{scope.Count:N0}</td>"
                + $"<td class=\"n\">{mean:F4}</td>"
                + $"<td class=\"n\">{scope.Files:N0}</td>"
                + $"<td class=\"n\">{scope.Median:F3}</td><td class=\"n\">{scope.P90:F3}</td>"
                + $"<td class=\"n\">{scope.P99:F2}</td><td class=\"n\">{scope.Max:F2}</td>"
                + $"{ReportPage.BarCell(debounce)}</tr>");
        }

        ReportPage.TableEnd(html);
    }

    /// <summary>What the run was over and what it left behind — context, so it goes last.</summary>
    private static void RunContext(
        StringBuilder html, PerfSweep sweep, IReadOnlyDictionary<string, int> worldCounts, Memory memory,
        int cachedHeaders)
    {
        // Per world, because a .gsc and a .csc are separate universes to the database, and "980
        // files" hides which of the two the time went to.
        html.AppendLine("<h2>Files by world</h2>");
        ReportPage.TableStart(html, null,
        [
            new Column("world", "server (.gsc) or client (.csc) scripts, which the database keeps apart"),
            new Column("files", "files of that world in the sweep"),
        ]);
        foreach ( KeyValuePair<string, int> world in worldCounts.OrderByDescending(static w => w.Value) )
        {
            html.AppendLine($"<tr><td>{ReportPage.Escape(world.Key)}</td><td class=\"n\">{world.Value}</td></tr>");
        }

        if ( sweep == PerfSweep.Analysis )
        {
            html.AppendLine($"<tr><td>headers held in the insert cache</td><td class=\"n\">{cachedHeaders}</td></tr>");
        }

        ReportPage.TableEnd(html);

        html.AppendLine("<h2>Memory after the sweep</h2>");
        html.AppendLine("<div class=\"sub\">Sampled after a forced gen2 collection, so this is what is RETAINED.</div>");
        ReportPage.TableStart(html, null, [new Column("pool"), new Column("MB", "megabytes"), new Column("what it means")]);
        MemoryRow(html, "managed live", memory.ManagedLive, "the object graph still reachable");
        MemoryRow(html, "heap size", memory.HeapSize, "what the GC has carved out");
        MemoryRow(html, "committed", memory.Committed, "backed by real memory");
        MemoryRow(html, "fragmented", memory.Fragmented, "holes in the heap, mostly large-object");
        MemoryRow(html, "working set", memory.WorkingSet, "what the OS reports for the process");
        html.AppendLine($"<tr><td>collections</td><td class=\"n\">{memory.Gen0}/{memory.Gen1}/{memory.Gen2}</td>"
            + "<td>gen0 / gen1 / gen2</td></tr>");
        ReportPage.TableEnd(html);
    }

    /// <summary>
    /// Rows of files. The phase columns appear only for the analysis sweep: every other sweep leaves
    /// them at zero, and a column of zeros reads as "free" rather than "not measured".
    /// </summary>
    private static void ItemTable(
        StringBuilder html, string? id, IReadOnlyList<Item> rows, string root, bool phases, bool legend = true)
    {
        List<Column> columns =
        [
            new Column("ms", "this file's time, from one timed run after a warm-up. " + ReportPage.OneReadingNote),
            new Column("KB", "the file's size on disk"),
            new Column("ms/KB", "ms divided by KB. High on a small file means time that grows faster than its size"),
        ];

        if ( phases )
        {
            columns.Add(new Column("lex", "ms spent lexing"));
            columns.Add(new Column("pre", "ms spent preprocessing"));
            columns.Add(new Column("parse", "ms spent parsing"));
            columns.Add(new Column("extract", "ms spent extracting symbols"));
        }

        columns.Add(new Column("file", "path under the corpus root"));
        ReportPage.TableStart(html, id, columns, legend);

        foreach ( Item row in rows )
        {
            StringBuilder line = new();
            line.Append($"<tr><td class=\"n\">{row.Milliseconds:F2}</td>");
            line.Append($"<td class=\"n\">{row.Bytes / 1024.0:F1}</td>");
            line.Append($"<td class=\"n\">{row.MillisecondsPerKilobyte:F2}</td>");

            if ( phases )
            {
                line.Append($"<td class=\"n\">{row.Lex:F2}</td><td class=\"n\">{row.Preprocess:F2}</td>");
                line.Append($"<td class=\"n\">{row.Parse:F2}</td><td class=\"n\">{row.Extract:F2}</td>");
            }

            line.Append($"<td class=\"path\"><code>{ReportPage.Escape(ReportPage.Relative(row.Path, root))}</code></td></tr>");
            html.AppendLine(line.ToString());
        }

        ReportPage.TableEnd(html);
    }

    private static void GroupTable(StringBuilder html, string? id, IReadOnlyList<FileGroup> rows, string root, bool legend = true)
    {
        ReportPage.TableStart(html, id,
        [
            new Column("max ms", "this file's slowest completion request. " + ReportPage.OneReadingNote),
            new Column("median ms", "its median request"),
            new Column("total ms", "all of its requests added up"),
            new Column("requests", "requests timed in this file: one at file scope and up to ten at call sites"),
            new Column("KB", "the file's size on disk"),
            new Column("file", "path under the corpus root"),
        ], legend);

        foreach ( FileGroup row in rows )
        {
            html.AppendLine($"<tr><td class=\"n\">{row.Max:F2}</td><td class=\"n\">{row.Median:F2}</td>"
                + $"<td class=\"n\">{row.Total:F2}</td><td class=\"n\">{row.Requests}</td>"
                + $"<td class=\"n\">{row.Bytes / 1024.0:F1}</td>"
                + $"<td class=\"path\"><code>{ReportPage.Escape(ReportPage.Relative(row.Path, root))}</code></td></tr>");
        }

        ReportPage.TableEnd(html);
    }

    private static List<FileGroup> GroupByFile(IReadOnlyList<Item> items)
    {
        List<FileGroup> groups = [];

        foreach ( IGrouping<string, Item> file in items.GroupBy(static i => i.Path, StringComparer.OrdinalIgnoreCase) )
        {
            List<double> sorted = [.. file.Select(static i => i.Milliseconds).Order()];
            groups.Add(new FileGroup(
                file.Key,
                file.First().Bytes,
                sorted.Count,
                Percentile(sorted, 0.50),
                sorted[^1],
                sorted.Sum()));
        }

        return groups;
    }

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    /// <summary>
    /// The run as JSON beside its page.
    ///
    /// It exists so the cross-game aggregate can be built at all: the sweep is two xUnit facts
    /// (BO3 has its own fixture, every other game shares one), they run in whatever order the runner
    /// chooses, and neither can see the other's results in process. Writing each game's numbers to
    /// disk and rebuilding the aggregate from whatever is present makes the aggregate independent of
    /// that ordering — and correct even when only one game is run.
    /// </summary>
    private static void WriteSummary(
        string directory, string name, string generatedAt, IReadOnlyList<Item> items, string root,
        List<double> sorted, IReadOnlyList<SubPhaseRow> subPhases, EntryCounts? entries)
    {
        double lex = items.Sum(static i => i.Lex);
        double pre = items.Sum(static i => i.Preprocess);
        double par = items.Sum(static i => i.Parse);
        double ext = items.Sum(static i => i.Extract);

        // Enough to find a file that is a hotspot in more than one game without carrying the whole
        // run. Worst reading PER FILE, since a completion sweep has up to eleven items per file and
        // a top 50 of items would be five files listed ten times.
        List<FileRow> topFiles = [.. items
            .GroupBy(static i => i.Path, StringComparer.OrdinalIgnoreCase)
            .Select(static file => file.MaxBy(static i => i.Milliseconds)!)
            .OrderByDescending(static i => i.Milliseconds)
            .Take(50)
            .Select(i => new FileRow(ReportPage.Relative(i.Path, root), i.Milliseconds, i.Bytes))];

        GameSummary summary = new(
            name,
            root,
            generatedAt,
            items.Count,
            sorted.Sum(),
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.90),
            Percentile(sorted, 0.99),
            sorted.Count > 0 ? sorted[^1] : 0,
            lex, pre, par, ext,
            [.. subPhases],
            topFiles,
            entries);

        string path = Path.Combine(directory, $"gscode-perf-{name}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(summary, s_json));
    }

    /// <summary>
    /// Rebuilds the hub: an index of every report page on disk, then every JSON sidecar's numbers,
    /// one section per sweep kind — a lint pass and a parse are different measurements, and a table that ranks them
    /// against each other compares nothing.
    ///
    /// Runs after EVERY sweep rather than once at the end, because there is no "end" a test can hook:
    /// whichever finishes last produces the complete page, and the ones before it produce correct
    /// partial ones. Each row carries its own timestamp, and a sidecar far older than the newest is
    /// marked stale and kept out of the cross-game tables rather than silently compared.
    /// </summary>
    public static void WriteAggregate(string directory)
    {
        List<GameSummary> games = [];
        foreach ( string file in Directory.EnumerateFiles(directory, "gscode-perf-*.json") )
        {
            try
            {
                if ( JsonSerializer.Deserialize<GameSummary>(File.ReadAllText(file)) is GameSummary summary )
                {
                    games.Add(summary);
                }
            }
            catch ( JsonException )
            {
                // A half-written sidecar from an interrupted run must not take the aggregate down.
            }
        }

        SortedSet<string> gameNames = new(StringComparer.Ordinal);
        foreach ( GameSummary game in games )
        {
            gameNames.Add(BaseGameOf(game.Game));
        }

        foreach ( string page in Directory.EnumerateFiles(directory, "gscode-*.html") )
        {
            string name = Path.GetFileNameWithoutExtension(page);
            foreach ( string prefix in new[] { "gscode-sweep-", "gscode-lint-budget-" } )
            {
                if ( name.StartsWith(prefix, StringComparison.Ordinal) )
                {
                    gameNames.Add(name[prefix.Length..]);
                }
            }
        }

        if ( gameNames.Count == 0 )
        {
            return;
        }

        DateTime newest = games.Count == 0 ? DateTime.MinValue : games.Max(static g => RunAt(g));
        HashSet<string> stale = new(StringComparer.Ordinal);
        foreach ( GameSummary game in games )
        {
            if ( IsStale(RunAt(game), newest) )
            {
                stale.Add(game.Game);
            }
        }

        // Known sweeps in pipeline order, then anything else a sidecar names — a retired sweep's
        // leftovers still show, under their own heading.
        List<string> kinds = [AnalysisKind, "lints", "completion"];
        foreach ( string kind in games.Select(static g => KindOf(g.Game)).Distinct().Order(StringComparer.Ordinal) )
        {
            if ( !kinds.Contains(kind) )
            {
                kinds.Add(kind);
            }
        }

        StringBuilder html = new();
        ReportPage.Head(html, "GSCode reports - all games");
        html.AppendLine("<h1>Reports - all games</h1>");
        PageIndex(html, directory, gameNames);

        if ( games.Count == 0 )
        {
            ReportPage.Foot(html);
            ReportPage.Save(Path.Combine(directory, ReportPage.AllGamesPage), html);
            return;
        }

        html.AppendLine("<h2>Perf sweeps</h2>");
        html.AppendLine($"<div class=\"sub\">{games.Count} sidecar(s) in <code>{ReportPage.Escape(directory)}</code>, "
            + $"newest run at {ReportPage.Escape(newest.ToString("s", CultureInfo.InvariantCulture))}. A game not in "
            + "the latest sweep keeps its previous numbers; one far older than the newest is dimmed, "
            + "marked stale, and left out of the cross-game tables.</div>");

        List<string> contents = [];
        foreach ( string kind in kinds )
        {
            if ( games.Any(g => KindOf(g.Game) == kind) )
            {
                contents.Add($"<a href=\"#{ReportPage.Escape(kind)}\">{ReportPage.Escape(KindHeading(kind))}</a>");
            }
        }

        html.AppendLine($"<nav>{string.Join(" · ", contents)}</nav>");

        foreach ( string kind in kinds )
        {
            List<GameSummary> ofKind = [.. games
                .Where(g => KindOf(g.Game) == kind)
                .OrderBy(static g => BaseGameOf(g.Game), StringComparer.Ordinal)];

            if ( ofKind.Count == 0 )
            {
                continue;
            }

            List<GameSummary> fresh = [.. ofKind.Where(g => !stale.Contains(g.Game))];

            html.AppendLine($"<h2 id=\"{ReportPage.Escape(kind)}\">{ReportPage.Escape(KindHeading(kind))}</h2>");
            KindTable(html, directory, kind, ofKind, stale);
            HotspotTable(html, fresh);
            SubPhasePivot(html, fresh);
        }

        ReportPage.Foot(html);
        ReportPage.Save(Path.Combine(directory, ReportPage.AllGamesPage), html);
    }

    /// <summary>
    /// Every page each game has on disk, so the hub reaches the diagnostic sweeps and lint budgets
    /// too — they are written by other suites and nothing else links to them.
    /// </summary>
    private static void PageIndex(StringBuilder html, string directory, IEnumerable<string> gameNames)
    {
        List<Column> columns = [new Column("game", "the game the pages are for")];
        columns.AddRange(ReportPage.GamePages("game").Select(static page => new Column(
            page.Label, $"when the {page.Label} page was last written; click to open it. A dash means it has not been run")));
        ReportPage.TableStart(html, null, columns);

        foreach ( string game in gameNames )
        {
            StringBuilder line = new();
            line.Append($"<tr><td><code>{ReportPage.Escape(game)}</code></td>");

            foreach ( PageLink page in ReportPage.GamePages(game) )
            {
                if ( File.Exists(Path.Combine(directory, page.File)) )
                {
                    string written = File.GetLastWriteTime(Path.Combine(directory, page.File))
                        .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                    line.Append($"<td><a href=\"{ReportPage.Escape(page.File)}\">{ReportPage.Escape(written)}</a></td>");
                }
                else
                {
                    line.Append("<td>-</td>");
                }
            }

            line.Append("</tr>");
            html.AppendLine(line.ToString());
        }

        ReportPage.TableEnd(html);

        if ( File.Exists(Path.Combine(directory, ReportPage.ScalePage)) )
        {
            html.AppendLine($"<div class=\"sub\">Scale runs: <a href=\"{ReportPage.ScalePage}\">{ReportPage.ScalePage}</a> "
                + "- Markdown, written to be pasted into PERF.md.</div>");
        }
    }

    private static DateTime RunAt(GameSummary game)
    {
        if ( DateTime.TryParse(game.GeneratedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime value) )
        {
            return value;
        }

        return DateTime.MinValue;
    }

    /// <summary>
    /// Whether a sidecar is too old to sit beside the newest run as if it were current. A stale one
    /// stays on the page, dimmed and labelled, but is left out of the hotspot and sub-phase tables so
    /// it cannot be read as part of today's comparison.
    /// </summary>
    private static bool IsStale(DateTime runAt, DateTime newest)
    {
        // TODO(user): decide the staleness rule. See the conversation for the trade-offs.
        return false;
    }

    private static void KindTable(
        StringBuilder html, string directory, string kind, IReadOnlyList<GameSummary> games, HashSet<string> stale)
    {
        bool phases = kind == AnalysisKind;
        bool entries = games.Any(static g => g.Entries is not null);

        string unit = UnitOf(kind);
        string one = unit == "requests" ? "request" : "file";
        List<Column> columns =
        [
            new Column("game", "click to open that game's page"),
            new Column(unit, $"{unit} timed"),
            new Column("total ms", $"every {one}'s time added up"),
            new Column("median", $"ms for one {one}: half took less"),
            new Column("p90", $"ms for one {one}: 90% took less"),
            new Column("p99", $"ms for one {one}: 99% took less"),
            new Column("max", $"ms for the single slowest {one}. " + ReportPage.OneReadingNote),
        ];

        if ( phases )
        {
            columns.Add(new Column("lex", "lexing's share of the four stages' time"));
            columns.Add(new Column("pre", "preprocessing's share"));
            columns.Add(new Column("parse", "parsing's share"));
            columns.Add(new Column("extract", "symbol extraction's share"));
        }

        if ( entries )
        {
            columns.Add(new Column("median entries", "median length of a completion list"));
            columns.Add(new Column("over 500 entries", "share of requests returning over 500 entries: the only ones that reach the "
                + "expensive store query"));
        }

        columns.Add(new Column("run at", "when the sweep ran"));
        ReportPage.TableStart(html, null, columns);

        foreach ( GameSummary game in games )
        {
            bool isStale = stale.Contains(game.Game);
            string page = ReportPage.PerfPage(game.Game);
            string name = File.Exists(Path.Combine(directory, page))
                ? $"<a href=\"{ReportPage.Escape(page)}\"><code>{ReportPage.Escape(BaseGameOf(game.Game))}</code></a>"
                : $"<code>{ReportPage.Escape(BaseGameOf(game.Game))}</code>";

            StringBuilder line = new();
            line.Append(isStale ? "<tr class=\"stale\">" : "<tr>");
            line.Append($"<td>{name}</td>");
            line.Append($"<td class=\"n\">{game.Files}</td><td class=\"n\">{game.TotalMilliseconds:F0}</td>");
            line.Append($"<td class=\"n\">{game.Median:F2}</td><td class=\"n\">{game.P90:F2}</td>");
            line.Append($"<td class=\"n\">{game.P99:F2}</td><td class=\"n\">{game.Max:F2}</td>");

            if ( phases )
            {
                double total = game.Lex + game.Preprocess + game.Parse + game.Extract;
                line.Append($"<td class=\"n\">{Share(game.Lex, total)}</td>");
                line.Append($"<td class=\"n\">{Share(game.Preprocess, total)}</td>");
                line.Append($"<td class=\"n\">{Share(game.Parse, total)}</td>");
                line.Append($"<td class=\"n\">{Share(game.Extract, total)}</td>");
            }

            if ( entries )
            {
                if ( game.Entries is null )
                {
                    line.Append("<td class=\"n\">-</td><td class=\"n\">-</td>");
                }
                else
                {
                    line.Append($"<td class=\"n\">{game.Entries.Median:F0}</td>");
                    line.Append($"<td class=\"n\">{Share(game.Entries.OverFiveHundred, game.Entries.Requests)}</td>");
                }
            }

            string label = isStale ? " <span class=\"tag\">stale</span>" : "";
            line.Append($"<td>{ReportPage.Escape(game.GeneratedAt)}{label}</td></tr>");
            html.AppendLine(line.ToString());
        }

        ReportPage.TableEnd(html);
    }

    /// <summary>
    /// The point of an all-games view. A file slow in ONE game is that game's problem; the same file
    /// slow in several is a shared-lineage script exercising one of our code paths, and fixing it
    /// pays out everywhere at once. Matched within one sweep kind only, and counted by DISTINCT game:
    /// cod4 ships both maps\_destructible_types.gsc and maps\mp\_destructible_types.gsc.
    /// </summary>
    private static void HotspotTable(StringBuilder html, IReadOnlyList<GameSummary> games)
    {
        if ( games.Select(static g => BaseGameOf(g.Game)).Distinct().Count() < 2 )
        {
            return;
        }

        Dictionary<string, Dictionary<string, double>> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach ( GameSummary game in games )
        {
            string baseGame = BaseGameOf(game.Game);
            foreach ( FileRow file in game.TopFiles )
            {
                string name = Path.GetFileName(file.Path);
                if ( !byName.TryGetValue(name, out Dictionary<string, double>? worst) )
                {
                    worst = new(StringComparer.Ordinal);
                    byName[name] = worst;
                }

                if ( !worst.TryGetValue(baseGame, out double previous) || file.Milliseconds > previous )
                {
                    worst[baseGame] = file.Milliseconds;
                }
            }
        }

        List<KeyValuePair<string, Dictionary<string, double>>> recurring = [.. byName
            .Where(static pair => pair.Value.Count > 1)
            .OrderByDescending(static pair => pair.Value.Count)
            .ThenByDescending(static pair => pair.Value.Values.Sum())];

        html.AppendLine("<h3>Hotspots in more than one game</h3>");
        if ( recurring.Count == 0 )
        {
            html.AppendLine("<div class=\"sub\">None — no file in one game's slowest 50 shares a name with "
                + "a file in another's.</div>");
            return;
        }

        html.AppendLine("<div class=\"sub\">Matched by file NAME across each game's slowest 50, worst "
            + "reading per game. The lineage shares script names, so these are usually the same file "
            + "evolved.</div>");
        ReportPage.TableStart(html, null,
        [
            new Column("file", "a file name, matched across games"),
            new Column("games", "how many games have it among their 50 slowest"),
            new Column("worst ms", "its slowest reading in any of those games"),
            new Column("where", "each game's reading"),
        ]);

        foreach ( KeyValuePair<string, Dictionary<string, double>> row in recurring )
        {
            string where = string.Join(", ", row.Value
                .OrderByDescending(static hit => hit.Value)
                .Select(static hit => $"{hit.Key} {hit.Value:F1} ms"));

            html.AppendLine($"<tr><td class=\"path\"><code>{ReportPage.Escape(row.Key)}</code></td>"
                + $"<td class=\"n\">{row.Value.Count}</td><td class=\"n\">{row.Value.Values.Max():F1}</td>"
                + $"<td>{ReportPage.Escape(where)}</td></tr>");
        }

        ReportPage.TableEnd(html);
    }

    /// <summary>
    /// One row per scope, one column per game, holding the worst single file — the number a keystroke
    /// pays — so the same rule can be compared across dialects at a glance. The corpus total is in
    /// each cell's tooltip; a cell at or over the debounce is highlighted.
    /// </summary>
    private static void SubPhasePivot(StringBuilder html, IReadOnlyList<GameSummary> games)
    {
        List<GameSummary> instrumented = [.. games.Where(static g => g.SubPhases.Count > 0)];
        if ( instrumented.Count == 0 )
        {
            return;
        }

        Dictionary<string, Dictionary<string, SubPhaseRow>> byScope = new(StringComparer.Ordinal);
        foreach ( GameSummary game in instrumented )
        {
            foreach ( SubPhaseRow scope in game.SubPhases )
            {
                if ( !byScope.TryGetValue(scope.Name, out Dictionary<string, SubPhaseRow>? perGame) )
                {
                    perGame = new(StringComparer.Ordinal);
                    byScope[scope.Name] = perGame;
                }

                perGame[game.Game] = scope;
            }
        }

        html.AppendLine("<h3>Sub-phases, worst file per game</h3>");
        html.AppendLine($"<div class=\"sub\">Max ms per file for each scope. Highlighted cells reach "
            + $"half of the {AnalysisTiming.DebounceMilliseconds} ms debounce or more; hover for the corpus total.</div>");

        List<Column> columns = [new Column("scope", "one lint rule or measured step")];
        columns.AddRange(instrumented.Select(static g => new Column(
            BaseGameOf(g.Game), $"ms in {BaseGameOf(g.Game)}'s single slowest file for this scope; hover a cell for its corpus total")));
        ReportPage.TableStart(html, null, columns);

        foreach ( KeyValuePair<string, Dictionary<string, SubPhaseRow>> scope in byScope
            .OrderByDescending(static pair => pair.Value.Values.Max(static row => row.Max)) )
        {
            StringBuilder line = new();
            line.Append($"<tr><td><code>{ReportPage.Escape(scope.Key)}</code></td>");

            foreach ( GameSummary game in instrumented )
            {
                if ( !scope.Value.TryGetValue(game.Game, out SubPhaseRow? row) )
                {
                    line.Append("<td class=\"n\">-</td>");
                    continue;
                }

                string hot = row.Max >= AnalysisTiming.DebounceMilliseconds / 2.0 ? " hot" : "";
                string title = $"total {row.Milliseconds:F0} ms, {row.Count:N0} calls, {row.Files:N0} files, "
                    + $"median {row.Median:F3} ms, p99 {row.P99:F2} ms";
                line.Append($"<td class=\"n{hot}\" title=\"{ReportPage.Escape(title)}\">{row.Max:F2}</td>");
            }

            line.Append("</tr>");
            html.AppendLine(line.ToString());
        }

        ReportPage.TableEnd(html);
    }

    private static string Share(double part, double whole)
    {
        return whole > 0 ? $"{part / whole * 100:F0}%" : "-";
    }

    private static void MemoryRow(StringBuilder html, string label, long bytes, string meaning)
    {
        html.AppendLine($"<tr><td>{ReportPage.Escape(label)}</td><td class=\"n\">{bytes / 1024.0 / 1024.0:F1}</td>"
            + $"<td>{ReportPage.Escape(meaning)}</td></tr>");
    }

    private static double Percentile(List<double> sorted, double fraction)
    {
        if ( sorted.Count == 0 )
        {
            return 0;
        }

        int index = (int)Math.Clamp(Math.Round(fraction * (sorted.Count - 1)), 0, sorted.Count - 1);
        return sorted[index];
    }

    /// <summary>
    /// The per-lint keystroke budget as a page, written by <c>LintBudgetTests</c>.
    ///
    /// A separate page from the perf sweep's, because it answers a different question with
    /// different data. The sweep's sub-phase table is fed by <c>PerfTracker</c> and is therefore
    /// EMPTY without the instrumentation flag; this is fed by <c>LintTimings</c> and is always
    /// there. It also carries what an aggregate cannot: which FILE produced each rule's worst
    /// reading, so a budget failure can be opened rather than only read.
    /// </summary>
    public static void WriteLintBudget(string outputPath, LintBudgetReport report)
    {
        StringBuilder html = new();
        ReportPage.Head(html, $"GSCode lint budget - {report.Game}");

        string directory = Path.GetDirectoryName(outputPath)!;
        ReportPage.GameNav(html, directory, report.Game, "lint budget");

        html.AppendLine($"<h1>Per-lint keystroke budget - {ReportPage.Escape(report.Game)}</h1>");
        html.AppendLine($"<div class=\"sub\">{report.FileCount:N0} files from "
            + $"<code>{ReportPage.Escape(report.CorpusRoot)}</code>. Each file's lint pass timed once after a warm "
            + "pass, per rule. Every figure is one FILE's cost - the quantity a keystroke pays - not a "
            + "corpus total.</div>");

        html.AppendLine("<div class=\"stats\">");
        ReportPage.Stat(html, "debounce", $"{report.DebounceMilliseconds:F0} ms");
        ReportPage.Stat(html, "per-rule budget", $"{report.PerRuleBudgetMilliseconds:F0} ms");
        ReportPage.Stat(html, "watch line", $"{report.WatchMilliseconds:F0} ms");
        ReportPage.Stat(html, "pass median", $"{report.Median:F2} ms");
        ReportPage.Stat(html, "pass p99", $"{report.P99:F2} ms");
        ReportPage.Stat(html, "pass max", $"{report.Max:F2} ms");
        html.AppendLine("</div>");

        html.AppendLine("<h2>Rules, by worst single file</h2>");
        html.AppendLine("<div class=\"sub\">The budget is on the worst file: a rule is allowed to be slow "
            + "in total across a corpus and is not allowed to be slow on one keystroke. <code>all-files ms</code> "
            + "is there for contrast - the two rankings disagree often.</div>");
        ReportPage.TableStart(html, null,
        [
            new Column("rule", "one lint rule. lint.FlowTyper.InferValues is the type inference the type-aware rules "
                + "share, timed on its own"),
            new Column("worst file", "the file this rule was slowest on, in this run"),
            new Column("worst-file ms", "the rule's time on that file - the number the budget checks. "
                + ReportPage.OneReadingNote),
            new Column("% of debounce", $"worst-file ms against the {report.DebounceMilliseconds:F0} ms keystroke debounce"),
            new Column("status", $"ok; watch above {report.WatchMilliseconds:F0} ms; OVER BUDGET above "
                + $"{report.PerRuleBudgetMilliseconds:F0} ms"),
            new Column("all-files ms", "the rule's time added up over every file: a corpus total, not what one "
                + "keystroke pays"),
            new Column("files", "files the rule ran in"),
        ]);

        foreach ( LintRuleCost rule in report.Rules )
        {
            string status = rule.Max > report.PerRuleBudgetMilliseconds
                ? "OVER BUDGET"
                : rule.Max > report.WatchMilliseconds ? "watch" : "ok";

            html.AppendLine($"<tr><td><code>{ReportPage.Escape(rule.Name)}</code></td>"
                + $"<td class=\"path\"><code>{ReportPage.Escape(ReportPage.Relative(rule.WorstPath, report.CorpusRoot))}</code></td>"
                + $"<td class=\"n\">{rule.Max:F2}</td>"
                + ReportPage.BarCell(rule.Max / report.DebounceMilliseconds * 100)
                + $"<td>{ReportPage.Escape(status)}</td>"
                + $"<td class=\"n\">{rule.Total:F0}</td>"
                + $"<td class=\"n\">{rule.Files}</td></tr>");
        }

        ReportPage.TableEnd(html);

        html.AppendLine("<h2>Slowest files, and where their time went</h2>");
        html.AppendLine("<div class=\"sub\">The whole pass for one file, with its three most expensive "
            + "rules. This is where a rule's worst reading can be checked against the file that "
            + "produced it.</div>");
        ReportPage.TableStart(html, null,
        [
            new Column("whole-pass ms", "every rule's time on this file, added up. " + ReportPage.OneReadingNote),
            new Column("% of debounce", $"whole-pass ms against the {report.DebounceMilliseconds:F0} ms keystroke debounce"),
            new Column("file", "path under the corpus root"),
            new Column("where it went", "its three most expensive rules"),
        ]);

        foreach ( LintFileCost file in report.SlowestFiles )
        {
            string breakdown = string.Join(", ", file.Rules
                .OrderByDescending(static rule => rule.Milliseconds)
                .Take(3)
                .Select(static rule => $"{rule.Name} {rule.Milliseconds:F2} ms"));

            html.AppendLine($"<tr><td class=\"n\">{file.Milliseconds:F2}</td>"
                + ReportPage.BarCell(file.Milliseconds / report.DebounceMilliseconds * 100)
                + $"<td class=\"path\"><code>{ReportPage.Escape(ReportPage.Relative(file.Path, report.CorpusRoot))}</code></td>"
                + $"<td>{ReportPage.Escape(breakdown)}</td></tr>");
        }

        ReportPage.TableEnd(html);
        ReportPage.Foot(html);
        ReportPage.Save(outputPath, html);

        WriteAggregate(directory);
    }

}
