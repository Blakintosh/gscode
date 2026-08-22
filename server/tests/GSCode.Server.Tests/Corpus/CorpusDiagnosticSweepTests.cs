using System.Collections.Concurrent;
using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
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
[Collection(GameProfileCollection.Name)]
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

    /// <summary>
    /// Marks a finding the editor does NOT show, because the rule that produced it stands down on
    /// this game. Carried in the message so it cannot be separated from the finding, and so the
    /// report — which groups by message — labels every site rather than a heading somebody scrolls
    /// past.
    /// </summary>
    private const string UngatedPrefix = "[NOT SHOWN — rule gated off for this game] ";

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

    /// <summary>
    /// Indexes one game's corpus, then lints every file against the finished database.
    ///
    /// MEMOIZED per game. Four tests in this class want the same sweep of the same games, and each
    /// redid the full index and lint — BO3 three times over, CoD4 and MW2 twice. The work is
    /// deterministic and read-only, so the second asker can have the first one's answer; that alone
    /// was about three minutes of a thirteen-minute run. Safe in a static: every corpus class shares
    /// one collection, so nothing runs concurrently with it, and a sweep never mutates what it reads.
    /// </summary>
    private static readonly Dictionary<string, List<Finding>> SweepCache = new(StringComparer.Ordinal);

    private static async Task<List<Finding>> SweepAsync(Target target)
    {
        if ( SweepCache.TryGetValue(target.Profile.ShortName, out List<Finding>? cached) )
        {
            return cached;
        }

        PathResolver resolver = target.Resolver();
        PhysicalFileSystem fileSystem = new();
        NameTable names = new();
        ScriptDatabase database = new();

        WorkspaceIndexer indexer = new(database, () => resolver, fileSystem, names);
        await indexer.IndexAsync(IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None);

        BuiltinApiSet builtins = BuiltinApiSet.Load(ApiDirectory);
        ObjectFields objectFields = ObjectFields.Load(ApiDirectory);

        // Every gate lifted, and the findings folded into this same report MARKED.
        //
        // A gate exists so the EDITOR does not blame a user for a hole in OUR data. The sweep is
        // offline and reports to us, so the same caution here only hides those holes from the people
        // who curate them — and a finding kept in a separate artifact is a finding nobody opens.
        // The marker therefore travels in the message rather than being left implicit; the report
        // groups by message, so it survives grouping and labels every site.
        //
        // This is how a gate gets re-examined. WaW and BO1 stand down because their libraries lack
        // engine functions, not because their scripts are wrong, and the only way to know when that
        // has stopped being true is to keep counting. Both flags move together because both describe
        // the library rather than the scripts.
        GameProfile asIfTrusted = target.Profile with
        {
            HasCompleteBuiltinLibrary = true,
            HasReliableBuiltinSignatures = true,
        };

        // Nothing to lift where the gates are already open, and nothing to compare against where the
        // game ships no library at all. Running the extra pass there costs a second lint of every
        // file to build a set that would be discarded as duplicates.
        bool anyGateShut = !target.Profile.HasCompleteBuiltinLibrary
            || !target.Profile.HasReliableBuiltinSignatures;

        bool liftGates = anyGateShut && builtins.For(ScriptLanguage.Gsc).Count > 0;

        // Per FILE, in parallel, exactly as the indexer analyses them. That the production indexer
        // already drives this same pipeline through Parallel.ForEachAsync at ProcessorCount - 1 is
        // what says this is safe: NameTable and InsertCache are ConcurrentDictionary-backed,
        // PathResolver holds only its config and file system, and an indexed record is immutable.
        ConcurrentBag<Finding> collected = [];
        ParallelOptions options = new() { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };

        await Parallel.ForEachAsync(target.Scripts(), options, (path, token) =>
        {
            ScriptLanguage language = ScriptAnalysis.LanguageFromPath(path);

            try
            {
                ParseResult result = target.Analyze(path, resolver, names);

                // EVERYTHING the editor would show: the file's own parse and semantic
                // diagnostics as well as the cross-file lints. Reporting lints alone hid the
                // 4000-series entirely -- precache rules, default-parameter rules, duplicate
                // functions -- which is most of what a user actually sees underlined.
                List<Finding> forFile = [];
                foreach ( Diagnostic diagnostic in WorkspaceLints.Analyze(
                    result, language, path, database, resolver, builtins, objectFields) )
                {
                    forFile.Add(new Finding(
                        diagnostic.Code,
                        diagnostic.Severity,
                        diagnostic.Message,
                        path,
                        diagnostic.Range.Start.Line,
                        diagnostic.Range.Start.Character));
                }

                if ( liftGates && (language == ScriptLanguage.Gsc || language == ScriptLanguage.Csc) )
                {
                    // Anything the real pipeline already reported is not repeated, so a marked
                    // finding means "suppressed on this game" rather than "run twice".
                    HashSet<(GscDiagnosticCode Code, int Line, int Character)> alreadyReported =
                        [.. forFile.Select(f => (f.Code, f.Line, f.Character))];

                    foreach ( Diagnostic diagnostic in Ungated(
                        result, database, resolver, builtins, path, language, asIfTrusted) )
                    {
                        if ( !alreadyReported.Add(
                            (diagnostic.Code, diagnostic.Range.Start.Line, diagnostic.Range.Start.Character)) )
                        {
                            continue;
                        }

                        forFile.Add(new Finding(
                            diagnostic.Code,
                            diagnostic.Severity,
                            UngatedPrefix + diagnostic.Message,
                            path,
                            diagnostic.Range.Start.Line,
                            diagnostic.Range.Start.Character));
                    }
                }

                foreach ( Finding finding in forFile )
                {
                    collected.Add(finding);
                }
            }
            catch ( Exception )
            {
                // Crashes are the lex/parse gate's business, not this one's.
            }

            return ValueTask.CompletedTask;
        });

        // Sorted, because a ConcurrentBag returns them in whatever order threads finished while the
        // report shows "e.g. <first site>" — an unstable order would make two runs of one corpus
        // look like different findings.
        List<Finding> findings =
        [
            .. collected.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Character)
                .ThenBy(f => (int)f.Code),
        ];

        SweepCache[target.Profile.ShortName] = findings;
        return findings;
    }

    /// <summary>
    /// The rules that stand down on a game whose builtin library we do not trust, run as though we
    /// did — 5013/5014 with the unverified half lifted, 5023's arity bound, and 5026's scope check.
    ///
    /// Each of these is gated for the same reason and reports the same thing when lifted: not that
    /// the scripts are wrong, but that our data for that game is short. 5014's own documentation
    /// says a corpus sweep of it IS the candidate list for the builtins the API is missing, ranked
    /// by how often they are called — and until now nothing swept it, so that list was assembled by
    /// hand.
    ///
    /// A game with no library at all still reports nothing from the two builtin rules, and that is
    /// correct rather than an oversight: there is no data to compare a name against.
    /// </summary>
    private static IEnumerable<Diagnostic> Ungated(
        ParseResult result,
        ScriptDatabase database,
        PathResolver resolver,
        BuiltinApiSet builtins,
        string path,
        ScriptLanguage language,
        GameProfile asIfTrusted)
    {
        LanguageStore store = database.StoreFor(language);
        BuiltinApi languageBuiltins = builtins.For(language);
        string contextId = ScriptDatabase.ContextIdOf(resolver.GetContext(path));

        return FunctionResolutionLint.Analyze(
                result, store, contextId, path, languageBuiltins, asIfTrusted,
                judgeUnverifiedBuiltins: true, resolver: resolver)
            .Concat(ArgumentCountLint.Analyze(result, store, contextId, path, languageBuiltins, asIfTrusted))
            .Concat(IncludeUsageLint.Analyze(
                result, store, language, resolver, path,
                builtins.EngineNamesFor(language), contextId, asIfTrusted));
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
            List<Finding> findings = await AsGameAsync(target.Profile, () => SweepAsync(target));

            WriteReport(target, findings);
            ReportToConsole(target, findings);
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> with a game selected, and puts the previous one back.
    ///
    /// The active profile has to MOVE with a sweep. Several lints take an optional profile and fall
    /// back to <c>GameProfile.Active</c>, and the indexer enumerates through
    /// <c>Active.ScriptGlobs</c> — so sweeping CoD4 while BO3 is active measures BO3's rules against
    /// CoD4's scripts and reports the difference as defects.
    ///
    /// Global state, hence the restore — and hence this class must not run beside anything that reads
    /// <c>Active</c>, which <see cref="GameProfileCollection"/> is what actually guarantees. It used
    /// to be guaranteed by nothing at all.
    /// </summary>
    private static async Task<T> AsGameAsync<T>(GameProfile game, Func<Task<T>> body)
    {
        GameProfile previous = GameProfile.Active;
        try
        {
            GameProfile.Select(game.ShortName);
            return await body();
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
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
    public async Task NoStockScriptWritesToAnEngineGlobal()
    {
        // 5035 is an Error, so the bar is zero across every corpus on the machine — not BO3 alone.
        // The rule reads its names from the profile, which is exactly the part a sweep has to check:
        // `world` is a global in BO3 and an ordinary local name in CoD4, and a rule that got that
        // backwards would report working code in one game and nothing at all in the other.
        List<Target> targets = Targets();
        if ( targets.Count == 0 )
        {
            _output.WriteLine("SKIPPED: no corpus configured (see GSCODE_CORPUS_<GAME>).");
            return;
        }

        List<Finding> reported = [];

        foreach ( Target target in targets )
        {
            List<Finding> findings = await AsGameAsync(target.Profile, () => SweepAsync(target));

            _output.WriteLine($"{target.Profile.ShortName}: {target.Scripts().Count} scripts swept");
            reported.AddRange(findings.Where(f => f.Code == GscDiagnosticCode.CannotAssignToGlobalObject));
        }

        Assert.Empty(reported.Select(f => $"{Path.GetFileName(f.Path)}:{f.Line + 1}: {f.Message}"));
    }

    [Fact]
    public async Task NoStockCallIsReportedMissingAnInclude()
    {
        // The #include counterpart to NoNamespaceIsReportedUnimported, over every include game whose
        // engine names 5026 will actually judge: CoD4 by its own verified library, MW2 by CoD4's
        // standing in for the one it does not ship. WaW and BO1 are gated off, and the sweep reports
        // what the rule WOULD say there under the [NOT SHOWN] marker — counted, never asserted,
        // because those games' libraries are the thing at fault rather than their scripts.
        //
        // The stock scripts compile, so every hit is a false positive in ours until proven
        // otherwise. Reported by name so a failure says WHICH function rather than a count.
        // Off the shared factory, so this selection cannot drift from how the other sweeps are built.
        // BO3 falls out on ImportStyle alone.
        List<Target> judged =
        [
            .. Targets().Where(target =>
                target.Profile.ImportStyle == ImportStyle.Include
                && target.Profile.HasTrustedEngineNames),
        ];

        if ( judged.Count == 0 )
        {
            _output.WriteLine("SKIPPED: no include-dialect corpus configured (see GSCODE_CORPUS_<GAME>).");
            return;
        }

        List<Finding> reported = [];

        foreach ( Target target in judged )
        {
            List<Finding> findings = await AsGameAsync(target.Profile, () => SweepAsync(target));

            _output.WriteLine($"{target.Profile.ShortName}: {target.Scripts().Count} scripts swept");
            reported.AddRange(findings.Where(f => f.Code == GscDiagnosticCode.FunctionNotIncluded));
        }

        // Grouped by message before the assertion, because the useful question on a failure is
        // never "how many" but "which NAME" — a shape shared by the top few is a language fact the
        // rule has not learned, rather than a defect rate in scripts that shipped.
        foreach ( IGrouping<string, Finding> group in reported
            .GroupBy(f => f.Message)
            .OrderByDescending(g => g.Count())
            .Take(15) )
        {
            _output.WriteLine($"{group.Count(),6}x  {group.Key}  e.g. {Path.GetFileName(group.First().Path)}");
        }

        Assert.Empty(reported.Select(f => $"{Path.GetFileName(f.Path)}:{f.Line + 1}: {f.Message}"));
    }

    /// <summary>
    /// The case 5026 was written for, against real scripts: a call into a file the caller does not
    /// include, on MW2 — the game with no builtin library of its own.
    ///
    /// Both halves matter. The rule must FIRE here, which the corpus sweep cannot show (the sweep
    /// asserts silence on files that are already correct), and it must fire on MW2 specifically,
    /// which it did not until the engine-name fallback let it judge names there at all.
    /// </summary>
    [Fact]
    public async Task ACallIntoAnUnincludedFileIsReportedOnMw2()
    {
        GameCorpus? mw2 = GameCorpusFixture.For(GameProfile.ByName("mw2")!);
        if ( mw2 is null )
        {
            _output.WriteLine("SKIPPED: %GSCODE_CORPUS_MW2% not found.");
            return;
        }

        // maps\mp\_utility.gsc includes common_scripts\utility and maps\mp\gametypes\_hud_util, and
        // nothing that reaches maps\mp\_loot.gsc, where initLootDisplay is declared.
        string path = Path.Combine(mw2.RawRoot, "maps", "mp", "_utility.gsc");
        if ( !File.Exists(path) )
        {
            _output.WriteLine($"SKIPPED: {path} not in this corpus copy.");
            return;
        }

        Diagnostic reported = await AsGameAsync(mw2.Profile, async () =>
        {
            PathResolver resolver = GameCorpusFixture.Resolver(mw2);
            NameTable names = new();
            ScriptDatabase database = new();
            WorkspaceIndexer indexer = new(database, () => resolver, new PhysicalFileSystem(), names);
            await indexer.IndexAsync(IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None);

            // The file as it ships, plus one call — which is exactly the edit that was reported as
            // going unreported.
            string edited = File.ReadAllText(path) + "\n\ncorpus_probe()\n{\n\tinitLootDisplay();\n}\n";
            ParseResult result = GameCorpusFixture.Analyze(mw2, path, resolver, names, edited);

            ImmutableArray<Diagnostic> lints = WorkspaceLints.LintsOnly(
                result, ScriptLanguage.Gsc, path, database, resolver,
                BuiltinApiSet.Load(ApiDirectory), ObjectFields.Load(ApiDirectory));

            return Assert.Single(lints.Where(d => d.Code == GscDiagnosticCode.FunctionNotIncluded));
        });

        Assert.Equal(DiagnosticSeverity.Error, reported.Severity);
        Assert.Contains("initLootDisplay", reported.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"maps\mp\_loot", reported.Message, StringComparison.OrdinalIgnoreCase);
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
