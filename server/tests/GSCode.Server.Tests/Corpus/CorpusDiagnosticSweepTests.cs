using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Workspace.Analysis;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using Xunit;
using Xunit.Abstractions;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// Runs the editor's ENTIRE diagnostic pipeline — parse diagnostics plus every cross-file lint —
/// over the whole stock corpus, and reports what it found grouped by code.
///
/// The stock scripts are known-good: they shipped in the game. So every diagnostic this produces
/// is either a real defect in the shipped code, or, far more often, a false positive in ours.
/// That makes the sweep the cheapest bug-finder available — a lint that misfires on 40 stock
/// files will misfire on the user's files too.
///
/// Reports rather than asserts a total, since the number changes as lints are added. It does
/// assert the cases already investigated and fixed, so they cannot come back.
/// </summary>
[Trait("Category", "Corpus")]
public class CorpusDiagnosticSweepTests
{
    private readonly ITestOutputHelper _output;

    public CorpusDiagnosticSweepTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private sealed record Finding(
        GscDiagnosticCode Code, DiagnosticSeverity Severity, string Message, string Path, int Line, int Character);

    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    /// <summary>One game to sweep: its profile, its raw root, and how to enumerate and analyse it.</summary>
    private sealed record Target(GameProfile Profile, string RawRoot, Func<PathResolver> Resolver,
        Func<IReadOnlyList<string>> Scripts, Func<string, PathResolver, NameTable, ParseResult> Analyze);

    /// <summary>
    /// Every game with a corpus on this machine. BO3 comes from CorpusFixture and the rest from
    /// GameCorpusFixture, which is the same split the two fixtures already have.
    /// </summary>
    private static List<Target> Targets()
    {
        List<Target> targets = [];

        if ( CorpusFixture.Available )
        {
            targets.Add(new Target(
                GameProfile.BlackOps3,
                CorpusFixture.RawRoot!,
                CorpusFixture.Resolver,
                CorpusFixture.Scripts,
                CorpusFixture.Analyze));
        }

        foreach ( GameCorpus corpus in GameCorpusFixture.Available() )
        {
            GameCorpus captured = corpus;
            targets.Add(new Target(
                captured.Profile,
                captured.RawRoot,
                () => GameCorpusFixture.Resolver(captured),
                () => GameCorpusFixture.Scripts(captured),
                (path, resolver, names) => GameCorpusFixture.Analyze(captured, path, resolver, names)));
        }

        return targets;
    }

    /// <summary>BO3 alone, for the two findings that were investigated against its scripts.</summary>
    private static Target Bo3Target()
    {
        return new Target(
            GameProfile.BlackOps3,
            CorpusFixture.RawRoot ?? "",
            CorpusFixture.Resolver,
            CorpusFixture.Scripts,
            CorpusFixture.Analyze);
    }

    /// <summary>Indexes one game's corpus, then lints every file against the finished database.</summary>
    private static async Task<List<Finding>> SweepAsync(Target target)
    {
        PathResolver resolver = target.Resolver();
        PhysicalFileSystem fileSystem = new();
        NameTable names = new();
        ScriptDatabase database = new();

        WorkspaceIndexer indexer = new(database, () => resolver, fileSystem, names);
        await indexer.IndexAsync(IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None);

        BuiltinApiSet builtins = BuiltinApiSet.Load(ApiDirectory);
        ObjectFields objectFields = ObjectFields.Load(ApiDirectory);

        List<Finding> findings = [];
        foreach ( string path in target.Scripts() )
        {
            ScriptLanguage language = ScriptAnalysis.LanguageFromPath(path);

            try
            {
                ParseResult result = target.Analyze(path, resolver, names);

                // EVERYTHING the editor would show: the file's own parse and semantic
                // diagnostics as well as the cross-file lints. Reporting lints alone hid the
                // 4000-series entirely -- precache rules, default-parameter rules, duplicate
                // functions -- which is most of what a user actually sees underlined.
                foreach ( Diagnostic diagnostic in WorkspaceLints.Analyze(
                    result, language, path, database, resolver, builtins, objectFields) )
                {
                    findings.Add(new Finding(
                        diagnostic.Code,
                        diagnostic.Severity,
                        diagnostic.Message,
                        path,
                        diagnostic.Range.Start.Line,
                        diagnostic.Range.Start.Character));
                }
            }
            catch ( Exception )
            {
                // Crashes are the lex/parse gate's business, not this one's.
            }
        }

        return findings;
    }

    [Fact]
    public async Task ReportEveryLintFiringOnStockScripts()
    {
        List<Target> targets = Targets();
        if ( targets.Count == 0 )
        {
            _output.WriteLine("SKIPPED: no corpus configured (see GSCODE_CORPUS_<GAME>).");
            return;
        }

        foreach ( Target target in targets )
        {
            // The active profile has to MOVE with the sweep. Several lints take an optional profile
            // and fall back to GameProfile.Active, and the indexer enumerates through
            // Active.ScriptGlobs — so sweeping CoD4 while BO3 is active measures BO3's rules against
            // CoD4's scripts and reports the difference as defects.
            //
            // Global state, so it is restored afterwards and this class must not run beside anything
            // that reads Active. Every corpus test carries Category=Corpus and is run as its own
            // pass for exactly this kind of reason.
            GameProfile previous = GameProfile.Active;
            List<Finding> findings;
            try
            {
                GameProfile.Select(target.Profile.ShortName);
                findings = await SweepAsync(target);
            }
            finally
            {
                GameProfile.Select(previous.ShortName);
            }

            WriteReport(target, findings);
            ReportToConsole(target, findings);
        }
    }

    /// <summary>The per-code breakdown for one game, most-reported first.</summary>
    private void ReportToConsole(Target target, List<Finding> findings)
    {
        int scripts = target.Scripts().Count;
        _output.WriteLine("");
        _output.WriteLine($"########## {target.Profile.ShortName}: {findings.Count} diagnostics across {scripts} scripts");
        _output.WriteLine("");

        foreach ( IGrouping<GscDiagnosticCode, Finding> group in findings
            .GroupBy(f => f.Code)
            .OrderByDescending(g => g.Count()) )
        {
            int files = group.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            _output.WriteLine($"=== {(int)group.Key} {group.Key}: {group.Count()} in {files} file(s) [{group.First().Severity}]");

            foreach ( IGrouping<string, Finding> byMessage in group
                .GroupBy(f => f.Message)
                .OrderByDescending(g => g.Count())
                .Take(8) )
            {
                _output.WriteLine($"      {byMessage.Count(),5}x  {byMessage.Key}");
                _output.WriteLine($"             e.g. {Path.GetFileName(byMessage.First().Path)}");
            }

            _output.WriteLine("");
        }
    }

    [Fact]
    public async Task NoNamespaceIsReportedUnimported()
    {
        // Every one of the 23 hits this once produced was a CLASS: `obj cscene::stop()` calls a
        // class method, and no #using can import a class. Nothing in the shipped scripts should
        // trip this lint now.
        if ( !CorpusFixture.Available )
        {
            _output.WriteLine("SKIPPED: %GSCODE_CORPUS_BO3% not found.");
            return;
        }

        List<Finding> findings = await SweepAsync(Bo3Target());

        // Projected to strings so a failure names the files rather than printing a type name.
        Assert.Empty(findings
            .Where(f => f.Code == GscDiagnosticCode.NamespaceNotImported)
            .Select(f => $"{Path.GetFileName(f.Path)}: {f.Message}"));
    }

    [Fact]
    public async Task MacroExpandedCallsKeepTheirImportAlive()
    {
        // `REGISTER_SYSTEM(...)` expands to `system::register(...)`, so a file using it does need
        // its #using of system_shared. Uses inside macro bodies were dropped outright, and 471
        // stock files were told that import was pointless.
        if ( !CorpusFixture.Available )
        {
            _output.WriteLine("SKIPPED: %GSCODE_CORPUS_BO3% not found.");
            return;
        }

        List<Finding> findings = await SweepAsync(Bo3Target());

        int wrong = findings.Count(f =>
            f.Code == GscDiagnosticCode.UnusedUsing
            && f.Message.Contains("system_shared", StringComparison.Ordinal)
            && File.ReadAllText(f.Path).Contains("REGISTER_SYSTEM", StringComparison.Ordinal));

        _output.WriteLine($"system_shared flagged on files invoking REGISTER_SYSTEM: {wrong}");

        // Measured at 3 after the fix, down from 471; the survivors invoke it inside code the
        // preprocessor drops. A ceiling rather than an exact count, since the corpus is whatever
        // mod-tools version is installed.
        Assert.True(wrong < 20, $"{wrong} files told their system_shared import is unused while invoking REGISTER_SYSTEM");
    }

    /// <summary>
    /// Writes one HTML report PER GAME.
    ///
    /// Separate files rather than one combined page, because the games are not comparable: their
    /// corpora differ in size by a factor of three, their dialects differ, and two of them have no
    /// builtin library to judge against. A single page invites reading across columns that do not
    /// mean the same thing.
    ///
    /// Written to the repository's gitignored <c>temp/</c> folder, so five reports are one click away
    /// in the editor rather than buried in the system temp path. The whole folder is ignored rather
    /// than the filenames: the contents are a snapshot of whichever game installs are on this
    /// machine, so committing one would be committing somebody's local state, and a filename pattern
    /// only protects the names somebody thought of.
    ///
    /// Falls back to the system temp folder when the repository root cannot be found — a packaged or
    /// relocated test run should still produce its reports somewhere. GSCODE_SWEEP_REPORT overrides
    /// the directory outright.
    /// </summary>
    private void WriteReport(Target target, List<Finding> findings)
    {
        string directory = Environment.GetEnvironmentVariable("GSCODE_SWEEP_REPORT") is string configured
            && configured.Length > 0
                ? configured
                : ScratchDirectory();

        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"gscode-sweep-{target.Profile.ShortName}.html");

        SweepReport.Write(
            path,
            [.. findings.Select(f => new SweepReport.Item(f.Code, f.Severity, f.Message, f.Path, f.Line, f.Character))],
            target.RawRoot);

        _output.WriteLine($"Report [{target.Profile.ShortName}]: {path}");
    }

    /// <summary>
    /// The repository's <c>temp/</c> folder, located by walking up from the test binaries looking for
    /// the <c>.git</c> directory. Falls back to the system temp folder if there is no repository
    /// above us, which is the case for a packaged run.
    /// </summary>
    private static string ScratchDirectory()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);

        while ( current is not null )
        {
            // A directory in a normal clone, a FILE in a worktree — which this repository is, so
            // checking only for the directory found nothing and silently fell back to system temp.
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
