using System.Globalization;
using System.Text;
using System.Text.Json;
using GSCode.Server.Handlers;

namespace GSCode.Server.Tests.Corpus;

/// <summary>One game's run, as written to the JSON sidecar and read back by the aggregate.</summary>
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
    List<FileRow> TopFiles);

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
/// The per-file timing sweep as a standalone HTML page, written beside the diagnostic sweep but
/// never by it: a perf run costs a second pass over every script, so it is opted into rather than
/// carried along.
///
/// Two tables, because they answer different questions. Slowest ABSOLUTE says where the wall-clock
/// went. Slowest PER KILOBYTE says where an algorithm is superlinear — a long file taking long is
/// arithmetic, a short file taking long is a bug.
/// </summary>
internal static class PerfReport
{
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

    public static void Write(
        string outputPath, string game, IReadOnlyList<Item> items, string corpusRoot,
        IReadOnlyDictionary<string, int> worldCounts, Memory memory, int cachedHeaders)
    {
        List<double> sorted = [.. items.Select(static i => i.Milliseconds).Order()];
        double total = sorted.Sum();
        int tailCount = (items.Count / 100) + 1;
        double tail = items.OrderByDescending(static i => i.Milliseconds).Take(tailCount).Sum(static i => i.Milliseconds);

        StringBuilder html = new();
        Head(html, $"GSCode perf sweep - {game}");

        html.AppendLine($"<h1>Analysis timing - {Escape(game)}</h1>");
        html.AppendLine($"<div class=\"sub\">{items.Count} files from <code>{Escape(corpusRoot)}</code>. "
            + "Each file lexed, preprocessed, parsed and extracted once to warm, then timed.</div>");

        html.AppendLine("<div class=\"stats\">");
        Stat(html, "total", $"{total:F0} ms");
        Stat(html, "median", $"{Percentile(sorted, 0.50):F2} ms");
        Stat(html, "p90", $"{Percentile(sorted, 0.90):F2} ms");
        Stat(html, "p99", $"{Percentile(sorted, 0.99):F2} ms");
        Stat(html, "max", $"{(sorted.Count > 0 ? sorted[^1] : 0):F2} ms");
        Stat(html, $"slowest {tailCount}", total > 0 ? $"{tail / total * 100:F1}% of total" : "-");
        html.AppendLine("</div>");

        // Per world, because a .gsc and a .csc are separate universes to the database, and "980
        // files" hides which of the two the time went to.
        html.AppendLine("<h2>Files by world</h2>");
        html.AppendLine("<table><tr><th>world</th><th>files</th></tr>");
        foreach ( KeyValuePair<string, int> world in worldCounts.OrderByDescending(static w => w.Value) )
        {
            html.AppendLine($"<tr><td>{Escape(world.Key)}</td><td class=\"n\">{world.Value}</td></tr>");
        }

        html.AppendLine($"<tr><td>headers held in the insert cache</td><td class=\"n\">{cachedHeaders}</td></tr>");
        html.AppendLine("</table>");

        html.AppendLine("<h2>Memory after the sweep</h2>");
        html.AppendLine("<div class=\"sub\">Sampled after a forced gen2 collection, so this is what is RETAINED.</div>");
        html.AppendLine("<table><tr><th>pool</th><th>MB</th><th>what it means</th></tr>");
        Row(html, "managed live", memory.ManagedLive, "the object graph still reachable");
        Row(html, "heap size", memory.HeapSize, "what the GC has carved out");
        Row(html, "committed", memory.Committed, "backed by real memory");
        Row(html, "fragmented", memory.Fragmented, "holes in the heap, mostly large-object");
        Row(html, "working set", memory.WorkingSet, "what the OS reports for the process");
        html.AppendLine($"<tr><td>collections</td><td class=\"n\">{memory.Gen0}/{memory.Gen1}/{memory.Gen2}</td>"
            + "<td>gen0 / gen1 / gen2</td></tr>");
        html.AppendLine("</table>");

        // Which PHASE the time went to, across the whole corpus. Only two of the four are
        // per-function work, so this is the level at which "why is this file slow" has an answer:
        // preprocess points at what it inserts, parse at its size and shape, extract at how much it
        // declares.
        double lex = items.Sum(static i => i.Lex);
        double pre = items.Sum(static i => i.Preprocess);
        double par = items.Sum(static i => i.Parse);
        double ext = items.Sum(static i => i.Extract);
        double phases = lex + pre + par + ext;

        html.AppendLine("<h2>Where the time goes, by phase</h2>");
        html.AppendLine("<table><tr><th>phase</th><th>ms</th><th>share</th></tr>");
        Phase(html, "lex", lex, phases);
        Phase(html, "preprocess", pre, phases);
        Phase(html, "parse", par, phases);
        Phase(html, "extract", ext, phases);
        html.AppendLine("</table>");

        // Sub-phases sit UNDER the four phases: extract.declarations contains extract.doc and
        // extract.body, so these do not sum to their parent and are not meant to.
        html.AppendLine("<h2>Sub-phases</h2>");
        IReadOnlyList<SubPhaseRow> subPhases = SubPhaseStats(items);
        if ( subPhases.Count == 0 )
        {
            html.AppendLine("<div class=\"sub\">Not instrumented. Rebuild with "
                + "<code>-p:GscodeInstrumentation=true</code> to record these — the scope calls are "
                + "<code>[Conditional]</code>, so an ordinary build carries none of them.</div>");
        }
        else
        {
            html.AppendLine("<div class=\"sub\">Nested inside the phases above, so they do not sum to "
                + "the total. <code>mean</code> is per call, which is where a per-declaration cost "
                + "that scales with FILE size shows up. <code>median</code> through <code>max</code> "
                + "are per FILE, over the files the scope ran in; "
                + $"<code>max % debounce</code> reads that worst file against the {AnalysisTiming.DebounceMilliseconds} ms "
                + "keystroke debounce, which is the only budget an interactive path has.</div>");
            html.AppendLine("<table><tr><th>scope</th><th>ms</th><th>calls</th><th>mean ms</th>"
                + "<th>files</th><th>median ms</th><th>p90 ms</th><th>p99 ms</th><th>max ms</th>"
                + "<th>max % debounce</th></tr>");
            foreach ( SubPhaseRow scope in subPhases )
            {
                double mean = scope.Count == 0 ? 0 : scope.Milliseconds / scope.Count;
                html.AppendLine($"<tr><td><code>{Escape(scope.Name)}</code></td>"
                    + $"<td class=\"n\">{scope.Milliseconds:F0}</td><td class=\"n\">{scope.Count:N0}</td>"
                    + $"<td class=\"n\">{mean:F4}</td>"
                    + $"<td class=\"n\">{scope.Files:N0}</td>"
                    + $"<td class=\"n\">{scope.Median:F3}</td><td class=\"n\">{scope.P90:F3}</td>"
                    + $"<td class=\"n\">{scope.P99:F2}</td><td class=\"n\">{scope.Max:F2}</td>"
                    + $"<td class=\"n\">{scope.Max / AnalysisTiming.DebounceMilliseconds * 100:F1}%</td></tr>");
            }

            html.AppendLine("</table>");
        }

        Table(html, "Slowest by absolute time",
            "Where the wall-clock went.",
            [.. items.OrderByDescending(static i => i.Milliseconds).Take(25)], corpusRoot);

        Table(html, "Slowest per kilobyte",
            "Files over 4 KB, ranked by time per KB. A short file high on this list is where to look "
            + "for superlinear behaviour - a long one is just long.",
            [.. items.Where(static i => i.Bytes > 4096)
                     .OrderByDescending(static i => i.MillisecondsPerKilobyte).Take(25)], corpusRoot);

        AllFilesTable(html, items, corpusRoot);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, html.ToString());

        WriteSummary(outputPath, game, items, corpusRoot, sorted, subPhases);
        WriteAggregate(Path.GetDirectoryName(outputPath)!);
    }

    /// <summary>
    /// Every file, sortable and filterable in the page.
    ///
    /// The two tables above are the curated answer to "what is slow"; this is the DATA, because a
    /// top-25 discards 97% of a run and no question outside the ones already asked can be answered
    /// from it. Sorting and filtering are a dozen lines of vanilla JS rather than a grid library —
    /// the page has to open from a file:// path with no network.
    /// </summary>
    private static void AllFilesTable(StringBuilder html, IReadOnlyList<Item> items, string root)
    {
        html.AppendLine("<h2>All files</h2>");
        html.AppendLine($"<div class=\"sub\">Every one of the {items.Count} files timed. Click a header "
            + "to sort; type to filter by path.</div>");
        html.AppendLine("<input id=\"q\" placeholder=\"filter by path...\">");
        html.AppendLine("<table id=\"all\"><thead><tr>"
            + "<th data-n=\"1\">ms</th><th data-n=\"1\">KB</th><th data-n=\"1\">ms/KB</th>"
            + "<th data-n=\"1\">lex</th><th data-n=\"1\">pre</th><th data-n=\"1\">parse</th>"
            + "<th data-n=\"1\">extract</th><th>file</th></tr></thead><tbody>");

        foreach ( Item row in items.OrderByDescending(static i => i.Milliseconds) )
        {
            html.AppendLine(
                $"<tr><td class=\"n\">{row.Milliseconds:F2}</td>"
                + $"<td class=\"n\">{row.Bytes / 1024.0:F1}</td>"
                + $"<td class=\"n\">{row.MillisecondsPerKilobyte:F2}</td>"
                + $"<td class=\"n\">{row.Lex:F2}</td><td class=\"n\">{row.Preprocess:F2}</td>"
                + $"<td class=\"n\">{row.Parse:F2}</td><td class=\"n\">{row.Extract:F2}</td>"
                + $"<td><code>{Escape(Relative(row.Path, root))}</code></td></tr>");
        }

        html.AppendLine("</tbody></table>");
        html.AppendLine("""
            <script>
            (function(){
              var q=document.getElementById('q'),t=document.getElementById('all'),b=t.tBodies[0];
              q.addEventListener('input',function(){
                var v=q.value.toLowerCase();
                for(var i=0;i<b.rows.length;i++){
                  var r=b.rows[i];
                  r.style.display=r.cells[7].textContent.toLowerCase().indexOf(v)<0?'none':'';
                }
              });
              t.tHead.addEventListener('click',function(e){
                var th=e.target.closest('th'); if(!th)return;
                var i=Array.prototype.indexOf.call(th.parentNode.children,th),
                    num=th.getAttribute('data-n')==='1',
                    dir=th.getAttribute('data-d')==='1'?-1:1;
                th.setAttribute('data-d',dir===1?'1':'0');
                var rows=Array.prototype.slice.call(b.rows);
                rows.sort(function(a,c){
                  var x=a.cells[i].textContent,y=c.cells[i].textContent;
                  return num?(parseFloat(x)-parseFloat(y))*dir:x.localeCompare(y)*dir;
                });
                for(var k=0;k<rows.length;k++)b.appendChild(rows[k]);
              });
            })();
            </script>
            """);
    }

    private static string Relative(string path, string root)
    {
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? path[root.Length..].TrimStart('\\', '/')
            : path;
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
        string outputPath, string game, IReadOnlyList<Item> items, string root,
        List<double> sorted, IReadOnlyList<SubPhaseRow> subPhases)
    {
        double lex = items.Sum(static i => i.Lex);
        double pre = items.Sum(static i => i.Preprocess);
        double par = items.Sum(static i => i.Parse);
        double ext = items.Sum(static i => i.Extract);

        GameSummary summary = new(
            game,
            root,
            DateTime.Now.ToString("s", CultureInfo.InvariantCulture),
            items.Count,
            sorted.Sum(),
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.90),
            Percentile(sorted, 0.99),
            sorted.Count > 0 ? sorted[^1] : 0,
            lex, pre, par, ext,
            [.. subPhases],
            // Enough to find a file that is a hotspot in more than one game without carrying the
            // whole run; the per-game page already holds every row.
            [.. items.OrderByDescending(static i => i.Milliseconds).Take(50)
                     .Select(i => new FileRow(Relative(i.Path, root), i.Milliseconds, i.Bytes))]);

        string path = Path.Combine(Path.GetDirectoryName(outputPath)!, $"gscode-perf-{game}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(summary, s_json));
    }

    /// <summary>
    /// Rebuilds the all-games page from every JSON sidecar in the directory.
    ///
    /// Runs after EVERY game rather than once at the end, because there is no "end" a test can hook:
    /// whichever game finishes last produces the complete page, and the ones before it produce
    /// correct partial ones. Each game's own timestamp is shown, so a sidecar left behind by an
    /// earlier run is visible as stale rather than silently averaged in.
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

        if ( games.Count == 0 )
        {
            return;
        }

        games.Sort(static (left, right) => string.CompareOrdinal(left.Game, right.Game));

        StringBuilder html = new();
        Head(html, "GSCode perf - all games");
        html.AppendLine("<h1>Analysis timing - all games</h1>");
        html.AppendLine($"<div class=\"sub\">{games.Count} game(s) with a sidecar in "
            + $"<code>{Escape(directory)}</code>. Each row carries its own run time — a game not in "
            + "the latest sweep keeps its previous numbers and says so.</div>");

        html.AppendLine("<h2>Per game</h2>");
        html.AppendLine("<table><tr><th>game</th><th>files</th><th>total ms</th><th>median</th>"
            + "<th>p99</th><th>max</th><th>lex</th><th>pre</th><th>parse</th><th>extract</th>"
            + "<th>run at</th></tr>");
        foreach ( GameSummary game in games )
        {
            double phases = game.Lex + game.Preprocess + game.Parse + game.Extract;
            html.AppendLine($"<tr><td><code>{Escape(game.Game)}</code></td>"
                + $"<td class=\"n\">{game.Files:N0}</td><td class=\"n\">{game.TotalMilliseconds:F0}</td>"
                + $"<td class=\"n\">{game.Median:F2}</td><td class=\"n\">{game.P99:F2}</td>"
                + $"<td class=\"n\">{game.Max:F2}</td>"
                + $"<td class=\"n\">{Share(game.Lex, phases)}</td>"
                + $"<td class=\"n\">{Share(game.Preprocess, phases)}</td>"
                + $"<td class=\"n\">{Share(game.Parse, phases)}</td>"
                + $"<td class=\"n\">{Share(game.Extract, phases)}</td>"
                + $"<td>{Escape(game.GeneratedAt)}</td></tr>");
        }

        html.AppendLine("</table>");

        // The point of an all-games view. A file slow in ONE game is that game's problem; the same
        // file slow in several is a shared-lineage script exercising one of our code paths, and
        // fixing it pays out everywhere at once.
        Dictionary<string, List<(string Game, double Milliseconds)>> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach ( GameSummary game in games )
        {
            foreach ( FileRow file in game.TopFiles )
            {
                string name = Path.GetFileName(file.Path);
                if ( !byName.TryGetValue(name, out List<(string, double)>? hits) )
                {
                    hits = [];
                    byName[name] = hits;
                }

                hits.Add((game.Game, file.Milliseconds));
            }
        }

        // DISTINCT games, not entries. A lineage reuses script names across directories — cod4 ships
        // both maps\_destructible_types.gsc and maps\mp\_destructible_types.gsc — so counting hits
        // listed the same game twice under a heading that promises more than one.
        List<KeyValuePair<string, List<(string Game, double Milliseconds)>>> recurring =
            [.. byName.Where(static pair => pair.Value.Select(static hit => hit.Game).Distinct().Count() > 1)
                      .OrderByDescending(static pair => pair.Value.Select(static hit => hit.Game).Distinct().Count())
                      .ThenByDescending(static pair => pair.Value.Sum(static hit => hit.Milliseconds))];

        html.AppendLine("<h2>Hotspots in more than one game</h2>");
        if ( recurring.Count == 0 )
        {
            html.AppendLine("<div class=\"sub\">None — no file in one game's slowest 50 shares a name "
                + "with a file in another's. With a single game swept this is expected.</div>");
        }
        else
        {
            html.AppendLine("<div class=\"sub\">Matched by file NAME across each game's slowest 50. "
                + "The lineage shares script names, so these are usually the same file evolved.</div>");
            html.AppendLine("<table><tr><th>file</th><th>games</th><th>where</th></tr>");
            foreach ( KeyValuePair<string, List<(string Game, double Milliseconds)>> row in recurring )
            {
                string where = string.Join(", ", row.Value
                    .OrderByDescending(static hit => hit.Milliseconds)
                    .Select(static hit => $"{hit.Game} {hit.Milliseconds:F1} ms"));
                int gameCount = row.Value.Select(static hit => hit.Game).Distinct().Count();
                html.AppendLine($"<tr><td><code>{Escape(row.Key)}</code></td>"
                    + $"<td class=\"n\">{gameCount}</td><td>{Escape(where)}</td></tr>");
            }

            html.AppendLine("</table>");
        }

        html.AppendLine("<h2>Sub-phases by game</h2>");
        if ( games.All(static g => g.SubPhases.Count == 0) )
        {
            html.AppendLine("<div class=\"sub\">Not instrumented. Rebuild with "
                + "<code>-p:GscodeInstrumentation=true</code>.</div>");
        }
        else
        {
            html.AppendLine("<table><tr><th>game</th><th>scope</th><th>ms</th><th>calls</th>"
                + "<th>mean ms</th><th>median ms</th><th>p99 ms</th><th>max ms</th>"
                + "<th>max % debounce</th></tr>");
            foreach ( GameSummary game in games )
            {
                foreach ( SubPhaseRow scope in game.SubPhases )
                {
                    double mean = scope.Count == 0 ? 0 : scope.Milliseconds / scope.Count;
                    html.AppendLine($"<tr><td><code>{Escape(game.Game)}</code></td>"
                        + $"<td><code>{Escape(scope.Name)}</code></td>"
                        + $"<td class=\"n\">{scope.Milliseconds:F0}</td>"
                        + $"<td class=\"n\">{scope.Count:N0}</td>"
                        + $"<td class=\"n\">{mean:F4}</td>"
                        + $"<td class=\"n\">{scope.Median:F3}</td>"
                        + $"<td class=\"n\">{scope.P99:F2}</td>"
                        + $"<td class=\"n\">{scope.Max:F2}</td>"
                        + $"<td class=\"n\">{scope.Max / AnalysisTiming.DebounceMilliseconds * 100:F1}%</td></tr>");
                }
            }

            html.AppendLine("</table>");
        }

        File.WriteAllText(Path.Combine(directory, "gscode-perf-all.html"), html.ToString());
    }

    private static string Share(double part, double whole)
    {
        return whole > 0 ? $"{part / whole * 100:F0}%" : "-";
    }

    /// <summary>
    /// Doctype, title and the shared stylesheet. Inline rather than a linked file because these
    /// pages are opened straight off disk and copied around; a report that needs a sibling CSS file
    /// to render is a report that arrives broken.
    /// </summary>
    private static void Head(StringBuilder html, string title)
    {
        html.AppendLine("<!doctype html><meta charset=\"utf-8\">");
        html.AppendLine($"<title>{Escape(title)}</title>");
        html.AppendLine("<style>");
        html.AppendLine("body{font:14px/1.5 system-ui,sans-serif;margin:2rem;max-width:70rem;color:#1a1a1a}");
        html.AppendLine("h1{font-size:1.4rem;margin-bottom:.2rem}h2{font-size:1.05rem;margin-top:2rem}");
        html.AppendLine(".sub{color:#666;margin-bottom:1.5rem}");
        html.AppendLine("table{border-collapse:collapse;width:100%;margin-top:.5rem}");
        html.AppendLine("th,td{text-align:left;padding:.35rem .6rem;border-bottom:1px solid #e5e5e5}");
        html.AppendLine("th{background:#fafafa;font-weight:600}td.n{text-align:right;font-variant-numeric:tabular-nums}");
        html.AppendLine("thead th{cursor:pointer;user-select:none;position:sticky;top:0}");
        html.AppendLine("code{font:13px ui-monospace,monospace}");
        html.AppendLine("#q{width:100%;padding:.4rem .6rem;font:13px ui-monospace,monospace;");
        html.AppendLine("border:1px solid #ddd;border-radius:3px;background:transparent;color:inherit}");
        html.AppendLine(".stats{display:flex;gap:2rem;flex-wrap:wrap;margin:1rem 0;padding:1rem;background:#fafafa;border:1px solid #eee}");
        html.AppendLine(".stat b{display:block;font-size:1.2rem;font-variant-numeric:tabular-nums}");
        html.AppendLine(".stat span{color:#666;font-size:.85rem}");
        html.AppendLine("@media(prefers-color-scheme:dark){body{background:#111;color:#eee}");
        html.AppendLine("th{background:#1c1c1c}th,td{border-color:#2a2a2a}.stats{background:#1a1a1a;border-color:#2a2a2a}");
        html.AppendLine("#q{border-color:#2a2a2a}.sub,.stat span{color:#999}}");
        html.AppendLine("</style>");
    }

    private static void Phase(StringBuilder html, string label, double ms, double total)
    {
        string share = total > 0 ? $"{ms / total * 100:F1}%" : "-";
        html.AppendLine($"<tr><td>{Escape(label)}</td><td class=\"n\">{ms:F0}</td>"
            + $"<td class=\"n\">{Escape(share)}</td></tr>");
    }

    private static void Row(StringBuilder html, string label, long bytes, string meaning)
    {
        html.AppendLine($"<tr><td>{Escape(label)}</td><td class=\"n\">{bytes / 1024.0 / 1024.0:F1}</td>"
            + $"<td>{Escape(meaning)}</td></tr>");
    }

    private static void Stat(StringBuilder html, string label, string value)
    {
        html.AppendLine($"<div class=\"stat\"><b>{Escape(value)}</b><span>{Escape(label)}</span></div>");
    }

    private static void Table(StringBuilder html, string title, string blurb, IReadOnlyList<Item> rows, string root)
    {
        html.AppendLine($"<h2>{Escape(title)}</h2>");
        html.AppendLine($"<div class=\"sub\">{Escape(blurb)}</div>");
        html.AppendLine("<table><tr><th>ms</th><th>KB</th><th>ms/KB</th>"
            + "<th>lex</th><th>pre</th><th>parse</th><th>extract</th><th>file</th></tr>");

        foreach ( Item row in rows )
        {
            string relative = row.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? row.Path[root.Length..].TrimStart('\\', '/')
                : row.Path;

            html.AppendLine(
                $"<tr><td class=\"n\">{row.Milliseconds:F1}</td>"
                + $"<td class=\"n\">{row.Bytes / 1024.0:F0}</td>"
                + $"<td class=\"n\">{row.MillisecondsPerKilobyte:F2}</td>"
                + $"<td class=\"n\">{row.Lex:F1}</td><td class=\"n\">{row.Preprocess:F1}</td>"
                + $"<td class=\"n\">{row.Parse:F1}</td><td class=\"n\">{row.Extract:F1}</td>"
                + $"<td><code>{Escape(relative)}</code></td></tr>");
        }

        html.AppendLine("</table>");
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
        Head(html, $"GSCode lint budget - {report.Game}");

        html.AppendLine($"<h1>Per-lint keystroke budget - {Escape(report.Game)}</h1>");
        html.AppendLine($"<div class=\"sub\">{report.FileCount:N0} files from "
            + $"<code>{Escape(report.CorpusRoot)}</code>. Each file's lint pass timed once after a warm "
            + "pass, per rule. Every figure is one FILE's cost - the quantity a keystroke pays - not a "
            + "corpus total.</div>");

        html.AppendLine("<div class=\"stats\">");
        Stat(html, "debounce", $"{report.DebounceMilliseconds:F0} ms");
        Stat(html, "per-rule budget", $"{report.PerRuleBudgetMilliseconds:F0} ms");
        Stat(html, "watch line", $"{report.WatchMilliseconds:F0} ms");
        Stat(html, "pass median", $"{report.Median:F2} ms");
        Stat(html, "pass p99", $"{report.P99:F2} ms");
        Stat(html, "pass max", $"{report.Max:F2} ms");
        html.AppendLine("</div>");

        html.AppendLine("<h2>Rules, by worst single file</h2>");
        html.AppendLine("<div class=\"sub\">The budget is on <code>max</code>: a rule is allowed to be "
            + "slow in total across a corpus and is not allowed to be slow on one keystroke. "
            + "<code>total</code> is there for contrast - the two disagree often, and the ranking by "
            + "total is the one that used to be the only one recorded.</div>");
        html.AppendLine("<table><tr><th>rule</th><th>max ms</th><th>% of debounce</th><th>status</th>"
            + "<th>worst file</th><th>total ms</th><th>files</th></tr>");

        foreach ( LintRuleCost rule in report.Rules )
        {
            string status = rule.Max > report.PerRuleBudgetMilliseconds
                ? "OVER BUDGET"
                : rule.Max > report.WatchMilliseconds ? "watch" : "ok";

            html.AppendLine($"<tr><td><code>{Escape(rule.Name)}</code></td>"
                + $"<td class=\"n\">{rule.Max:F2}</td>"
                + $"<td class=\"n\">{rule.Max / report.DebounceMilliseconds * 100:F1}%</td>"
                + $"<td>{Escape(status)}</td>"
                + $"<td><code>{Escape(Relative(rule.WorstPath, report.CorpusRoot))}</code></td>"
                + $"<td class=\"n\">{rule.Total:F0}</td>"
                + $"<td class=\"n\">{rule.Files:N0}</td></tr>");
        }

        html.AppendLine("</table>");

        html.AppendLine("<h2>Slowest files, and where their time went</h2>");
        html.AppendLine("<div class=\"sub\">The whole pass for one file, with its three most expensive "
            + "rules. This is where a rule's worst reading can be checked against the file that "
            + "produced it.</div>");
        html.AppendLine("<table><tr><th>ms</th><th>% of debounce</th><th>file</th>"
            + "<th>where it went</th></tr>");

        foreach ( LintFileCost file in report.SlowestFiles )
        {
            string breakdown = string.Join(", ", file.Rules
                .OrderByDescending(static rule => rule.Milliseconds)
                .Take(3)
                .Select(static rule => $"{rule.Name} {rule.Milliseconds:F2} ms"));

            html.AppendLine($"<tr><td class=\"n\">{file.Milliseconds:F2}</td>"
                + $"<td class=\"n\">{file.Milliseconds / report.DebounceMilliseconds * 100:F1}%</td>"
                + $"<td><code>{Escape(Relative(file.Path, report.CorpusRoot))}</code></td>"
                + $"<td>{Escape(breakdown)}</td></tr>");
        }

        html.AppendLine("</table>");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, html.ToString());
    }

    private static string Escape(string text)
    {
        return text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }
}
