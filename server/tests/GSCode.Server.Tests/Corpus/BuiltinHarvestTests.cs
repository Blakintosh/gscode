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
    }

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

        foreach ( (string path, ParseResult result) in parsed )
        {
            ScriptLanguage language = profile.LanguageFromPath(path);
            LanguageStore store = database.StoreFor(language);
            string contextId = ScriptDatabase.ContextIdOf(resolver.GetContext(path));

            foreach ( Diagnostic diagnostic in FunctionResolutionLint.Analyze(
                result, store, contextId, path, apiSet.For(language), profile) )
            {
                Dictionary<string, Candidate> bucket = diagnostic.Code == GscDiagnosticCode.BuiltinFunctionNotFound
                    ? builtinCandidates
                    : scriptMisses;

                string name = NameFrom(diagnostic);
                if ( !bucket.TryGetValue(name, out Candidate? candidate) )
                {
                    candidate = new Candidate();
                    bucket[name] = candidate;
                    candidate.FirstSite = $"{path}({diagnostic.Range.Start.Line + 1})";
                }

                candidate.Calls++;
                candidate.Files.Add(path);
            }
        }

        Report("BUILTIN CANDIDATES (unqualified, resolve to no script function and no API entry)", builtinCandidates);
        Report("SCRIPT MISSES (explicitly namespace- or path-qualified, resolve to nothing)", scriptMisses);

        _output.WriteLine("");
        _output.WriteLine($"Swept {scripts.Count} scripts: {builtinCandidates.Count} distinct builtin candidates, {scriptMisses.Count} distinct script misses.");
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
