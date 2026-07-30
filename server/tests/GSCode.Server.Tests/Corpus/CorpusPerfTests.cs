using System.Diagnostics;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Workspace.Resolution;
using Xunit;
using Xunit.Abstractions;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// Where analysis time actually goes, per FILE, over a real game's scripts.
///
/// A whole-run total answers nothing useful: 7,000 files at a few milliseconds each is the same
/// number as 6,900 fast ones and 100 pathological ones, and only the second is worth fixing. So this
/// times each file, then reports the distribution and the slowest by both absolute time and time per
/// kilobyte — the second being what separates a genuinely slow file from a merely large one.
///
/// Reporting, not asserting. A timing threshold on a machine of unknown speed is a flaky test; the
/// budget gates live in CorpusTests, which assert throughput the whole run has to meet. This exists
/// to be READ.
/// </summary>
[Trait("Category", "Perf")]
public class CorpusPerfTests
{
    private readonly ITestOutputHelper _output;

    public CorpusPerfTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void BlackOps3_WhereTheTimeGoes()
    {
        if ( !CorpusFixture.Available )
        {
            _output.WriteLine("SKIPPED: %GSCODE_CORPUS_BO3% not found - the perf sweep needs the game scripts.");
            return;
        }

        PathResolver resolver = CorpusFixture.Resolver();
        NameTable names = new();
        List<PerfReport.Item> timings = [];

        foreach ( string path in CorpusFixture.Scripts() )
        {
            timings.Add(Time(path, () => CorpusFixture.Analyze(path, resolver, names)));
        }

        Report("bo3", timings, CorpusFixture.RawRoot!);
    }

    [Fact]
    public void EveryOtherGame_WhereTheTimeGoes()
    {
        IReadOnlyList<GameCorpus> corpora = GameCorpusFixture.Available();
        if ( corpora.Count == 0 )
        {
            _output.WriteLine("SKIPPED: no %GSCODE_CORPUS_<GAME>% found - the perf sweep needs the game scripts.");
            return;
        }

        foreach ( GameCorpus corpus in corpora )
        {
            PathResolver resolver = GameCorpusFixture.Resolver(corpus);
            NameTable names = new();
            List<PerfReport.Item> timings = [];

            foreach ( string path in GameCorpusFixture.Scripts(corpus) )
            {
                timings.Add(Time(path, () => GameCorpusFixture.Analyze(corpus, path, resolver, names)));
            }

            Report(corpus.Profile.ShortName, timings, corpus.RawRoot);
        }
    }

    private static PerfReport.Item Time(string path, Func<ParseResult> analyse)
    {
        long bytes = new FileInfo(path).Length;

        // Once to warm the file cache and any lazily built table, so the measurement is of analysis
        // rather than of first-touch I/O.
        analyse();

        Stopwatch watch = Stopwatch.StartNew();
        analyse();
        watch.Stop();

        return new PerfReport.Item(path, watch.Elapsed.TotalMilliseconds, bytes);
    }

    private void Report(string game, List<PerfReport.Item> timings, string root)
    {
        if ( timings.Count == 0 )
        {
            return;
        }

        List<double> sorted = [.. timings.Select(static t => t.Milliseconds).Order()];
        double total = sorted.Sum();

        _output.WriteLine($"=== {game}: {timings.Count} files, {total:F0} ms total ===");
        _output.WriteLine(
            $"    median {Percentile(sorted, 0.50):F2} ms | p90 {Percentile(sorted, 0.90):F2} ms | "
            + $"p99 {Percentile(sorted, 0.99):F2} ms | max {sorted[^1]:F2} ms");

        // The tail's share of the total is the number that decides whether optimising outliers is
        // worth anything: a p99 ten times the median matters only if those files are a real slice.
        double tail = timings.OrderByDescending(static t => t.Milliseconds).Take(timings.Count / 100 + 1)
            .Sum(static t => t.Milliseconds);
        _output.WriteLine($"    slowest 1% account for {tail / total * 100:F1}% of the total");

        _output.WriteLine("    --- slowest by absolute time ---");
        foreach ( PerfReport.Item timing in timings.OrderByDescending(static t => t.Milliseconds).Take(10) )
        {
            _output.WriteLine(
                $"    {timing.Milliseconds,8:F1} ms  {timing.Bytes / 1024.0,7:F0} KB  "
                + $"{timing.MillisecondsPerKilobyte,6:F2} ms/KB  {Path.GetFileName(timing.Path)}");
        }

        // Slow FOR ITS SIZE is the interesting list: it is where an algorithm is superlinear rather
        // than where a file is simply long.
        _output.WriteLine("    --- slowest per kilobyte (files over 4 KB) ---");
        foreach ( PerfReport.Item timing in timings
            .Where(static t => t.Bytes > 4096)
            .OrderByDescending(static t => t.MillisecondsPerKilobyte)
            .Take(10) )
        {
            _output.WriteLine(
                $"    {timing.Milliseconds,8:F1} ms  {timing.Bytes / 1024.0,7:F0} KB  "
                + $"{timing.MillisecondsPerKilobyte,6:F2} ms/KB  {Path.GetFileName(timing.Path)}");
        }

        WriteReport(game, timings, root);
    }

    private static double Percentile(List<double> sorted, double fraction)
    {
        int index = (int)Math.Clamp(Math.Round(fraction * (sorted.Count - 1)), 0, sorted.Count - 1);
        return sorted[index];
    }

    /// <summary>
    /// Writes the page beside the diagnostic sweep's, under a DIFFERENT name so a perf run never
    /// overwrites a diagnostic report someone is still reading. <c>GSCODE_PERF_REPORT</c> overrides
    /// the directory, matching GSCODE_SWEEP_REPORT.
    /// </summary>
    private void WriteReport(string game, IReadOnlyList<PerfReport.Item> timings, string root)
    {
        string directory = Environment.GetEnvironmentVariable("GSCODE_PERF_REPORT") is string configured
            && configured.Length > 0
                ? configured
                : ScratchDirectory();

        string path = Path.Combine(directory, $"gscode-perf-{game}.html");
        PerfReport.Write(path, game, timings, root);
        _output.WriteLine($"Report [{game}]: {path}");
    }

    /// <summary>
    /// The repository's <c>temp/</c> folder, found by walking up to the <c>.git</c> entry — which is
    /// a directory in a clone and a FILE in a worktree, so both are checked. Falls back to the system
    /// temp folder when there is no repository above, as in a packaged run.
    /// </summary>
    private static string ScratchDirectory()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);

        while ( current is not null )
        {
            string git = Path.Combine(current.FullName, ".git");
            if ( Directory.Exists(git) || File.Exists(git) )
            {
                return Path.Combine(current.FullName, "temp");
            }

            current = current.Parent;
        }

        return Path.GetTempPath();
    }
}
