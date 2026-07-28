using System.Text.Json;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Workspace.Analysis;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Resolution;
using Xunit;
using Xunit.Abstractions;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// Sweeps the corpus for calls that resolve to neither a script function nor a known engine
/// function, and reports them ranked by how often they are called. This is the reason
/// <see cref="GscDiagnosticCode.BuiltinFunctionNotFound"/> is a separate code from
/// <see cref="GscDiagnosticCode.ScriptFunctionNotFound"/>: the unqualified failures are exactly the
/// candidates for builtins our API data is missing.
///
/// FREQUENCY IS THE DISAMBIGUATOR. An unqualified name that resolves to nothing is either a typo or
/// a missing builtin, and no static rule separates them — but a real engine function is called from
/// many files, while a typo appears once. The ranking is therefore the finding; the raw list is not.
///
/// Reports rather than asserts a count: the corpus is whatever mod-tools version is installed, so a
/// hard number would be brittle. The gate is that the sweep RUNS and the script-side stays clean.
/// </summary>
[Trait("Category", "Corpus")]
public class BuiltinHarvestTests
{
    private readonly ITestOutputHelper _output;

    public BuiltinHarvestTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private sealed class Candidate
    {
        public int Calls { get; set; }
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? FirstSite { get; set; }

        /// <summary>Distinct argument counts seen at call sites — the signature, observed.</summary>
        public SortedSet<int> ArgCounts { get; } = [];

        /// <summary>Whether it is ever called on a target (<c>self foo()</c>), i.e. a method.</summary>
        public bool CalledAsMethod { get; set; }

        /// <summary>Which worlds call it, from the call sites' own paths.</summary>
        public bool InMultiplayer { get; set; }
        public bool InSingleplayer { get; set; }
        public HashSet<string> Languages { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Sites { get; } = [];
    }

    /// <summary>
    /// A missing function, with everything the corpus can say about it. A name nothing documents has
    /// no page to read a signature from, so the call sites ARE the evidence: how many files want it,
    /// how many arguments they pass, whether they call it on a target, and which worlds it appears
    /// in. That is enough to decide whether it is a real engine function and what its shape is.
    /// </summary>
    private sealed record MissingFunction(
        string Name,
        string Kind,
        int Calls,
        int FileCount,
        IReadOnlyList<int> ObservedArgCounts,
        bool CalledAsMethod,
        string Access,
        IReadOnlyList<string> Languages,
        IReadOnlyList<string> Sites);

    private sealed record MissingReport(string Game, int ScriptsSwept, int Distinct, IReadOnlyList<MissingFunction> Functions);

    [Fact]
    public void HarvestUnresolvedBuiltinCandidates()
    {
        if ( !CorpusFixture.Available )
        {
            _output.WriteLine("SKIPPED: %TA_TOOLS_PATH%\\share\\raw not found.");
            return;
        }

        GameProfile profile = GameProfile.BlackOps3;
        BuiltinApiSet apiSet = LoadApi(profile);
        if ( apiSet.For(ScriptLanguage.Gsc).Count == 0 )
        {
            _output.WriteLine("SKIPPED: builtin API data did not load.");
            return;
        }

        IReadOnlyList<string> scripts = CorpusFixture.Scripts();
        PathResolver resolver = CorpusFixture.Resolver();
        NameTable names = new();

        // Index the corpus first: proving a name resolves to NOTHING requires the store to be
        // complete, unlike every other lint, which only ever asks whether a found thing is allowed.
        ScriptDatabase database = new();
        List<(string Path, ParseResult Result)> parsed = [];
        foreach ( string path in scripts )
        {
            ParseResult result = CorpusFixture.Analyze(path, resolver, names);
            parsed.Add((path, result));

            ResolutionContext context = resolver.GetContext(path);
            ScriptRecord record = ScriptDatabase.BuildRecord(
                result, context, isDirty: false, resolver.GetScriptRelativePath(path, context));
            database.StoreFor(record.Language).Upsert(record);
        }

        Dictionary<string, Candidate> builtinCandidates = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Candidate> scriptMisses = new(StringComparer.OrdinalIgnoreCase);

        Collect(parsed, database, resolver, apiSet, profile, CorpusFixture.RawRoot!, builtinCandidates, scriptMisses);

        Report("BUILTIN CANDIDATES (unqualified, resolve to no script function and no API entry)", builtinCandidates);
        Report("SCRIPT MISSES (explicitly namespace- or path-qualified, resolve to nothing)", scriptMisses);

        _output.WriteLine("");
        _output.WriteLine($"Swept {scripts.Count} scripts: {builtinCandidates.Count} distinct builtin candidates, {scriptMisses.Count} distinct script misses.");

        WriteReport(profile, scripts.Count, builtinCandidates, scriptMisses);
    }

    /// <summary>
    /// Writes the findings as JSON next to the harvest, so curating the library is a matter of
    /// reading a file rather than scraping test output.
    /// </summary>
    /// <summary>
    /// Writes the findings as JSON, one file per KIND. They are separate deliverables: the builtin
    /// list feeds curating the engine library, while the script list is about files the distribution
    /// did not ship. Mixing them means whoever is doing one job has to filter out the other's rows.
    /// </summary>
    private void WriteReport(
        GameProfile profile,
        int scriptsSwept,
        Dictionary<string, Candidate> builtinCandidates,
        Dictionary<string, Candidate> scriptMisses)
    {
        WriteOne(profile, scriptsSwept, builtinCandidates, "builtin", "missing_builtins");
        WriteOne(profile, scriptsSwept, scriptMisses, "script", "missing_script_functions");
    }

    private void WriteOne(
        GameProfile profile, int scriptsSwept, Dictionary<string, Candidate> bucket, string kind, string fileStem)
    {
        List<MissingFunction> functions = [.. Describe(bucket, kind)];

        // Most-wanted first: a real engine function is called from many files, a typo from one.
        functions.Sort(static (left, right) =>
        {
            int byFiles = right.FileCount.CompareTo(left.FileCount);
            return byFiles != 0 ? byFiles : right.Calls.CompareTo(left.Calls);
        });

        MissingReport report = new(profile.ShortName, scriptsSwept, functions.Count, functions);

        // Written into the PROJECT, not the build output: this is a work product to read and curate
        // from, and bin/ is wiped by a clean. Falls back to the binary's folder if the source tree
        // is not beside it (a packaged run).
        string directory = Path.Combine(FindProjectRoot() ?? AppContext.BaseDirectory, "harvest");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, $"{profile.ShortName}_{fileStem}.json");

        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            NewLine = "\n",
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        File.WriteAllText(file, json + "\n", new System.Text.UTF8Encoding(false));
        _output.WriteLine($"Report: {file} ({functions.Count})");
    }

    /// <summary>The test project folder, found by walking up to the one holding the .csproj.</summary>
    private static string? FindProjectRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while ( directory is not null )
        {
            if ( File.Exists(Path.Combine(directory.FullName, "GSCode.Server.Tests.csproj")) )
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static IEnumerable<MissingFunction> Describe(Dictionary<string, Candidate> bucket, string kind)
    {
        foreach ( KeyValuePair<string, Candidate> entry in bucket )
        {
            Candidate c = entry.Value;
            string access = c.InMultiplayer && c.InSingleplayer ? "both"
                : c.InMultiplayer ? "mp"
                : c.InSingleplayer ? "sp"
                : "unknown";

            yield return new MissingFunction(
                entry.Key, kind, c.Calls, c.Files.Count, [.. c.ArgCounts], c.CalledAsMethod,
                access, [.. c.Languages.OrderBy(static l => l, StringComparer.Ordinal)], c.Sites);
        }
    }

    /// <summary>Runs the lint over every parsed file and accumulates what it reports.</summary>
    private static void Collect(
        List<(string Path, ParseResult Result)> parsed,
        ScriptDatabase database,
        PathResolver resolver,
        BuiltinApiSet apiSet,
        GameProfile profile,
        string root,
        Dictionary<string, Candidate> builtinCandidates,
        Dictionary<string, Candidate> scriptMisses)
    {
        foreach ( (string path, ParseResult result) in parsed )
        {
            ScriptLanguage language = profile.LanguageFromPath(path);
            LanguageStore store = database.StoreFor(language);
            string contextId = ScriptDatabase.ContextIdOf(resolver.GetContext(path));

            foreach ( Diagnostic diagnostic in FunctionResolutionLint.Analyze(
                result, store, contextId, path, apiSet.For(language), profile,
                // The harvest is how a library gets measured, so it must see past the Verified gate.
                judgeUnverifiedBuiltins: true) )
            {
                Dictionary<string, Candidate> bucket = diagnostic.Code == GscDiagnosticCode.BuiltinFunctionNotFound
                    ? builtinCandidates
                    : scriptMisses;

                string name = NameFrom(diagnostic);
                // Relative to the corpus root: an absolute path names this machine, and the
                // report is meant to be read and shared.
                string site = $"{Path.GetRelativePath(root, path)}({diagnostic.Range.Start.Line + 1})";
                if ( !bucket.TryGetValue(name, out Candidate? candidate) )
                {
                    candidate = new Candidate();
                    bucket[name] = candidate;
                    candidate.FirstSite = site;
                }

                candidate.Calls++;
                candidate.Files.Add(path);
                candidate.Languages.Add(language.ToString());
                if ( candidate.Sites.Count < 5 )
                {
                    candidate.Sites.Add(site);
                }

                // Which world wants it, read off the call site's own path.
                if ( path.Contains(@"\mp\", StringComparison.OrdinalIgnoreCase) )
                {
                    candidate.InMultiplayer = true;
                }
                else if ( path.Contains(@"\sp\", StringComparison.OrdinalIgnoreCase) )
                {
                    candidate.InSingleplayer = true;
                }

                // A missing function has no page to read a signature from, so the call site is the
                // only evidence of its shape.
                foreach ( AstNode node in AstSearch.ChainAt(result.Tree.Root, diagnostic.Range.Start) )
                {
                    if ( node is CallNode call )
                    {
                        candidate.ArgCounts.Add(call.Arguments.Length);
                        if ( call.Target is not null )
                        {
                            candidate.CalledAsMethod = true;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// The same sweep for the games that keep their scripts outside the tools install. CoD4 is the
    /// one that matters today: its library was just rebuilt from the documentation pages, so this is
    /// what says which engine functions those pages do not cover.
    /// </summary>
    [Fact]
    public void HarvestPerGameCorpora()
    {
        IReadOnlyList<GameCorpus> corpora = GameCorpusFixture.Available();
        if ( corpora.Count == 0 )
        {
            _output.WriteLine("SKIPPED: no per-game corpora configured (set GSCODE_CORPUS_<GAME>).");
            return;
        }

        BuiltinApiSet apiSet;
        foreach ( GameCorpus corpus in corpora )
        {
            GameProfile profile = corpus.Profile;
            apiSet = LoadApi(profile);
            if ( apiSet.For(ScriptLanguage.Gsc).Count == 0 )
            {
                _output.WriteLine($"{profile.ShortName}: no builtin library, so only script misses are visible.");
            }

            IReadOnlyList<string> scripts = GameCorpusFixture.Scripts(corpus);
            PathResolver resolver = GameCorpusFixture.Resolver(corpus);
            NameTable names = new();

            ScriptDatabase database = new();
            List<(string Path, ParseResult Result)> parsed = [];
            foreach ( string path in scripts )
            {
                ParseResult result = GameCorpusFixture.Analyze(corpus, path, resolver, names);
                parsed.Add((path, result));

                ResolutionContext context = resolver.GetContext(path);
                ScriptRecord record = ScriptDatabase.BuildRecord(
                    result, context, isDirty: false, resolver.GetScriptRelativePath(path, context));
                database.StoreFor(record.Language).Upsert(record);
            }

            Dictionary<string, Candidate> builtinCandidates = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Candidate> scriptMisses = new(StringComparer.OrdinalIgnoreCase);
            Collect(parsed, database, resolver, apiSet, profile, corpus.RawRoot, builtinCandidates, scriptMisses);

            _output.WriteLine("");
            _output.WriteLine($"### {profile.ShortName}: swept {scripts.Count} scripts");
            Report("BUILTIN CANDIDATES", builtinCandidates);
            Report("SCRIPT MISSES", scriptMisses);
            WriteReport(profile, scripts.Count, builtinCandidates, scriptMisses);
        }
    }

    private void Report(string heading, Dictionary<string, Candidate> bucket)
    {
        _output.WriteLine("");
        _output.WriteLine($"=== {heading} — {bucket.Count} distinct ===");

        foreach ( KeyValuePair<string, Candidate> entry in bucket
            .OrderByDescending(static e => e.Value.Files.Count)
            .ThenByDescending(static e => e.Value.Calls)
            .Take(60) )
        {
            _output.WriteLine($"  {entry.Value.Files.Count,4} files {entry.Value.Calls,5} calls  {entry.Key}   [{entry.Value.FirstSite}]");
        }
    }

    private static string NameFrom(Diagnostic diagnostic)
    {
        // The name is the message's only quoted span.
        int open = diagnostic.Message.IndexOf('\'');
        int close = open < 0 ? -1 : diagnostic.Message.IndexOf('\'', open + 1);
        return open >= 0 && close > open ? diagnostic.Message[(open + 1)..close] : diagnostic.Message;
    }

    private static BuiltinApiSet LoadApi(GameProfile profile)
    {
        // The Api folder ships beside the server binary.
        string directory = Path.Combine(AppContext.BaseDirectory, "Api");
        return Directory.Exists(directory) ? BuiltinApiSet.Load(directory, profile) : new BuiltinApiSet(BuiltinApi.Empty, BuiltinApi.Empty);
    }
}
