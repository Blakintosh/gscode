using System.Text.Json;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;
using GSCode.Workspace.Api;
using GSCode.Workspace.Resolution;
using Xunit;
using Xunit.Abstractions;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// Measures what a game's SERVER library gets wrong when pointed at its CLIENT scripts, so a CSC
/// library can be built from the GSC one instead of from nothing.
///
/// WaW and BO1 document no client VM, so until this ran they shipped no <c>*_api_csc.json</c> at all and
/// a <c>.csc</c> file in those games offered no hover, no signature help and no completion —
/// <see cref="ApiLoader"/> returns <see cref="BuiltinApi.Empty"/> for a file that is not there. Their
/// GSC libraries are close to the right answer, because most engine functions exist on both VMs under
/// the same name, and this is what measures the difference so the field-data tool can correct for it.
///
/// THE ONE SYSTEMATIC DIFFERENCE IS THE LEADING <c>localClientNum</c>. Client scripts run one VM per
/// splitscreen client, so a client-side builtin that acts on a particular screen takes the client
/// index first: <c>VisionSetNaked( 0, "vampire_low" )</c> against the server's
/// <c>VisionSetNaked( "vampire_low" )</c>. Copying the GSC signature verbatim therefore under-declares
/// those by exactly one parameter, and the call sites say which ones: they pass one argument more
/// than the server form allows, and the extra leading argument is a small integer.
///
/// That is what this harvest counts. For every <c>.csc</c> call to a name the GSC library knows, it
/// compares the argument count against the union of the declared overloads and records the overflows
/// together with the text of the first argument. A name whose overflow is consistently one, whose
/// first argument is consistently <c>0</c>–<c>3</c> or something spelled like a client index, and
/// which appears across many files, is a <c>localClientNum</c> — and the fix is a parameter in the
/// data, not a diagnostic.
///
/// BO3 IS THE CONTROL, and the reason to trust any of this. It is the one game that ships a real
/// hand-documented <c>t7_api_csc.json</c>, in which 224 of 803 entries already name
/// <c>localClientNum</c> as their first parameter. Running the same GSC-library-on-CSC-scripts
/// experiment there produces a prediction that can be scored against that known answer, so the report
/// states the heuristic's precision before anyone applies it to a game where the answer is unknown.
///
/// Reports rather than asserts, for the same reason as <see cref="BuiltinHarvestTests"/>: the corpus
/// is whatever is configured locally, so a hard number would be brittle. The gate is that it runs.
///
/// The upper bound this leans on is deliberately NOT a production diagnostic — see
/// <c>ArgumentCountLint.InspectBuiltin</c>, which checks only the lower bound because the libraries
/// under-declare parameters. This is the harvest that comment points at: the overflow is evidence
/// about the DATA, and reporting it to a user as their mistake is what that decision ruled out.
/// </summary>
[Trait("Category", "Corpus")]
[Collection(GameProfileCollection.Name)]
public class ClientArityHarvestTests
{
    private readonly ITestOutputHelper _output;

    public ClientArityHarvestTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// The share of a name's over-supplied calls that must lead with a client index before the name
    /// is called one. Set from the BO3 control rather than by taste: at 0.8 the prediction is exactly
    /// right on every name BO3's own client library confirms, and the first wrong answer
    /// (<c>GetCharacterBodyRenderOptions</c>) sits at 0.25, among the names whose leading argument is
    /// genuinely something else. The band between is mixed, so it is reported and not predicted.
    /// </summary>
    private const double ClientIndexThreshold = 0.8;

    /// <summary>What the corpus says about one over-supplied builtin.</summary>
    private sealed class Candidate
    {
        public int DeclaredMax { get; set; }
        public int Calls { get; set; }
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SortedSet<int> ObservedCounts { get; } = [];

        /// <summary>Calls whose first argument is a bare 0–3 — a splitscreen client index.</summary>
        public int NumericFirst { get; set; }

        /// <summary>Calls whose first argument is spelled like a client index, e.g. <c>localClientNum</c>.</summary>
        public int NamedFirst { get; set; }

        /// <summary>Distinct first-argument texts, for reading the evidence rather than trusting the count.</summary>
        public HashSet<string> FirstArgs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Sites { get; } = [];
    }

    /// <summary>A server-library name that the shipped client scripts actually call.</summary>
    private sealed class Observed
    {
        public int Calls { get; set; }
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record ObservedFunction(string Name, int Calls, int FileCount, bool InControlLibrary);

    private sealed record ObservedReport(
        string Game,
        int ClientScriptsSwept,
        int ServerLibrarySize,
        int Observed,
        string Coverage,
        IReadOnlyList<ObservedFunction> Functions);

    private sealed record ArityFinding(
        string Name,
        int DeclaredMax,
        IReadOnlyList<int> ObservedArgCounts,
        int Calls,
        int FileCount,
        int NumericFirstArgCalls,
        int NamedFirstArgCalls,
        double ClientIndexConfidence,
        bool PredictedLocalClientNum,
        string? ControlAnswer,
        IReadOnlyList<string> FirstArgSamples,
        IReadOnlyList<string> Sites);

    private sealed record ArityReport(
        string Game,
        int ClientScriptsSwept,
        int Distinct,
        int PredictedLocalClientNum,
        string? Scoring,
        IReadOnlyList<ArityFinding> Functions);

    [Fact]
    public void HarvestClientArityEvidence()
    {
        List<(GameProfile Profile, IReadOnlyList<string> Scripts, Func<string, ParseResult> Analyze, string Root)> targets = [];

        // BO3 first: it is the control, and its report is what licenses reading the others.
        if ( CorpusFixture.Available )
        {
            PathResolver resolver = CorpusFixture.Resolver();
            NameTable names = new();
            targets.Add((
                GameProfile.BlackOps3,
                CorpusFixture.Scripts(),
                path => CorpusFixture.Analyze(path, resolver, names),
                CorpusFixture.RawRoot!));
        }

        foreach ( GameCorpus corpus in GameCorpusFixture.Available() )
        {
            if ( !corpus.Profile.HasClientScripts )
            {
                continue;
            }

            PathResolver resolver = GameCorpusFixture.Resolver(corpus);
            NameTable names = new();
            GameCorpus captured = corpus;
            targets.Add((
                corpus.Profile,
                GameCorpusFixture.Scripts(corpus),
                path => GameCorpusFixture.Analyze(captured, path, resolver, names),
                corpus.RawRoot));
        }

        if ( targets.Count == 0 )
        {
            _output.WriteLine("SKIPPED: no corpus with client scripts configured (set GSCODE_CORPUS_<GAME>).");
            return;
        }

        foreach ( (GameProfile profile, IReadOnlyList<string> scripts, Func<string, ParseResult> analyze, string root) in targets )
        {
            Harvest(profile, scripts, analyze, root);
        }
    }

    private void Harvest(
        GameProfile profile, IReadOnlyList<string> scripts, Func<string, ParseResult> analyze, string root)
    {
        string apiDirectory = Path.Combine(AppContext.BaseDirectory, "Api");
        BuiltinApi server = ApiLoader.Load(apiDirectory, ScriptLanguage.Gsc, profile);
        if ( server.Count == 0 )
        {
            _output.WriteLine($"{profile.ShortName}: no GSC library to hypothesise from, skipping.");
            return;
        }

        // The known answer, where there is one — and a DERIVED client library is not one. WaW's and
        // BO1's are generated from these very findings, so scoring against them would report a perfect
        // result no matter what the rule was. A real client library says so structurally rather than
        // by name: BO3's carries hundreds of functions its server library has never heard of, which is
        // something pruning and annotating a GSC library cannot produce.
        BuiltinApi client = ApiLoader.Load(apiDirectory, ScriptLanguage.Csc, profile);
        bool isSource = client.All.Any(function => server.Find(function.Name) is null);
        BuiltinApi control = isSource ? client : BuiltinApi.Empty;

        HashSet<string> knownClientIndexed = KnownLocalClientNum(control);

        Dictionary<string, Candidate> candidates = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Observed> observed = new(StringComparer.OrdinalIgnoreCase);
        int swept = 0;

        foreach ( string path in scripts )
        {
            if ( profile.LanguageFromPath(path) != ScriptLanguage.Csc )
            {
                continue;
            }

            swept++;
            ParseResult result = analyze(path);
            foreach ( AstNode element in result.Tree.Root.Elements )
            {
                Walk(element, result, server, path, root, candidates, observed);
            }
        }

        WriteReport(profile, swept, candidates, knownClientIndexed, control.Count);
        WriteObserved(profile, swept, observed, server, control);
    }

    private static void Walk(
        AstNode node,
        ParseResult result,
        BuiltinApi server,
        string path,
        string root,
        Dictionary<string, Candidate> candidates,
        Dictionary<string, Observed> observed)
    {
        if ( node is CallNode call )
        {
            Inspect(call, result, server, path, root, candidates, observed);
        }

        foreach ( AstNode child in AstSearch.ChildrenOf(node) )
        {
            Walk(child, result, server, path, root, candidates, observed);
        }
    }

    private static void Inspect(
        CallNode call,
        ParseResult result,
        BuiltinApi server,
        string path,
        string root,
        Dictionary<string, Candidate> candidates,
        Dictionary<string, Observed> observed)
    {
        // Only a bare name can be a builtin, and only unexpanded text is the author's — the same two
        // guards the production lint applies, for the same reasons.
        if ( call.Callee is not IdentifierNode identifier || CameFromMacro(call) )
        {
            return;
        }

        string name = identifier.Token.Text;
        if ( server.Find(name) is not BuiltinFunction builtin )
        {
            return;
        }

        // Every call, before the arity question: a name the client scripts actually call is a name
        // the client VM actually has, which is the evidence the library gets pruned against.
        if ( !observed.TryGetValue(name, out Observed? seen) )
        {
            seen = new Observed();
            observed[name] = seen;
        }

        seen.Calls++;
        seen.Files.Add(path);

        if ( builtin.Overloads.Length == 0 )
        {
            return;
        }

        // The union of the overloads: a name with a 1- and a 3-argument form accepts either, so the
        // widest is the only bound an overflow can be measured against.
        int declaredMax = 0;
        foreach ( BuiltinOverload overload in builtin.Overloads )
        {
            declaredMax = Math.Max(declaredMax, overload.Parameters.Length);
        }

        int supplied = call.Arguments.Length;
        if ( supplied <= declaredMax )
        {
            return;
        }

        if ( !candidates.TryGetValue(name, out Candidate? candidate) )
        {
            candidate = new Candidate { DeclaredMax = declaredMax };
            candidates[name] = candidate;
        }

        candidate.Calls++;
        candidate.Files.Add(path);
        candidate.ObservedCounts.Add(supplied);

        string first = TextOf(result, call.Arguments[0].Range);
        if ( first.Length > 0 && candidate.FirstArgs.Count < 12 )
        {
            candidate.FirstArgs.Add(first);
        }

        if ( IsClientIndexLiteral(first) )
        {
            candidate.NumericFirst++;
        }
        else if ( IsClientIndexName(first) )
        {
            candidate.NamedFirst++;
        }

        if ( candidate.Sites.Count < 5 )
        {
            candidate.Sites.Add($"{Path.GetRelativePath(root, path)}({identifier.Token.RootRange.Start.Line + 1})");
        }
    }

    /// <summary>
    /// A bare splitscreen client index. Four-player splitscreen means 0–3, and anything wider is some
    /// other integer argument that happens to lead — a flag, a count, a duration.
    /// </summary>
    private static bool IsClientIndexLiteral(string text)
    {
        return text.Length == 1 && text[0] is >= '0' and <= '3';
    }

    /// <summary>
    /// Spelled like a client index. The stock scripts thread the value through a parameter far more
    /// often than they write a literal, and that parameter is named for what it is.
    ///
    /// Underscores are stripped before matching, which is the whole reason this is not a set of exact
    /// names: the corpus writes the same concept as <c>localClientNum</c>, <c>client_num</c>,
    /// <c>int_client_num</c> and <c>GetLocalClientNumber()</c>, and only the first of those contains
    /// "clientnum" literally. Hungarian prefixes and a call wrapping the value are both common enough
    /// that a substring test after stripping beats any list.
    /// </summary>
    private static bool IsClientIndexName(string text)
    {
        string squashed = text.Replace("_", "", StringComparison.Ordinal);

        return squashed.Contains("clientnum", StringComparison.OrdinalIgnoreCase)
            || squashed.Contains("localclient", StringComparison.OrdinalIgnoreCase)
            || squashed.Contains("localplayernum", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The names a real client library already documents as taking a leading client index.</summary>
    private static HashSet<string> KnownLocalClientNum(BuiltinApi client)
    {
        HashSet<string> known = new(StringComparer.OrdinalIgnoreCase);
        foreach ( BuiltinFunction function in client.All )
        {
            foreach ( BuiltinOverload overload in function.Overloads )
            {
                if ( overload.Parameters.Length > 0
                    && IsClientIndexName(overload.Parameters[0].Name) )
                {
                    known.Add(function.Name);
                    break;
                }
            }
        }

        return known;
    }

    private static bool CameFromMacro(CallNode call)
    {
        if ( call.Callee is IdentifierNode identifier && identifier.Token.Provenance.DefinitionSite is not null )
        {
            return true;
        }

        foreach ( AstNode child in AstSearch.ChildrenOf(call) )
        {
            if ( child is IdentifierNode argument && argument.Token.Provenance.DefinitionSite is not null )
            {
                return true;
            }
        }

        return false;
    }

    private static string TextOf(ParseResult result, TextRange range)
    {
        int start = result.Text.GetOffset(range.Start);
        int end = result.Text.GetOffset(range.End);
        if ( start < 0 || end <= start || end > result.Text.Length )
        {
            return "";
        }

        return result.Text.Slice(start, end - start).ToString().Trim();
    }

    private void WriteReport(
        GameProfile profile,
        int swept,
        Dictionary<string, Candidate> candidates,
        HashSet<string> knownClientIndexed,
        int clientLibrarySize)
    {
        List<ArityFinding> findings = [];
        foreach ( KeyValuePair<string, Candidate> entry in candidates )
        {
            Candidate c = entry.Value;

            // How much of the overflow is explained by a leading client index. Note what is NOT
            // consulted: the SIZE of the overflow. Requiring it to be exactly one — the shape a single
            // extra leading parameter makes — reads well and is wrong, because the server library
            // under-declares its trailing parameters too and the total carries that error as well.
            // It rejected `StopLoopSound( 0, ent, 0 )` (three arguments against a declared one, every
            // call led by a client index) and cost the control eight names for no precision gained.
            double confidence = c.Calls == 0 ? 0 : (double)(c.NumericFirst + c.NamedFirst) / c.Calls;
            bool predicted = confidence >= ClientIndexThreshold;

            findings.Add(new ArityFinding(
                entry.Key,
                c.DeclaredMax,
                [.. c.ObservedCounts],
                c.Calls,
                c.Files.Count,
                c.NumericFirst,
                c.NamedFirst,
                Math.Round(confidence, 3),
                predicted,
                clientLibrarySize > 0 ? (knownClientIndexed.Contains(entry.Key) ? "localClientNum" : "no") : null,
                [.. c.FirstArgs.OrderBy(static a => a, StringComparer.OrdinalIgnoreCase)],
                c.Sites));
        }

        findings.Sort(static (left, right) =>
        {
            int byFiles = right.FileCount.CompareTo(left.FileCount);
            return byFiles != 0 ? byFiles : right.Calls.CompareTo(left.Calls);
        });

        // Score the heuristic wherever the answer is already known, so the games where it is not can
        // be read with a measured error rate rather than a hope.
        string? scoring = null;
        if ( clientLibrarySize > 0 )
        {
            int truePositives = findings.Count(static f => f.PredictedLocalClientNum && f.ControlAnswer == "localClientNum");
            int falsePositives = findings.Count(static f => f.PredictedLocalClientNum && f.ControlAnswer != "localClientNum");
            int missed = findings.Count(static f => !f.PredictedLocalClientNum && f.ControlAnswer == "localClientNum");
            int predicted = truePositives + falsePositives;
            double precision = predicted == 0 ? 0 : (double)truePositives / predicted;
            scoring = $"predicted {predicted}: {truePositives} correct, {falsePositives} wrong "
                + $"(precision {precision:P1}); {missed} known localClientNum names not predicted.";
        }

        int predictedCount = findings.Count(static f => f.PredictedLocalClientNum);

        _output.WriteLine("");
        _output.WriteLine($"### {profile.ShortName}: {swept} client scripts, {findings.Count} over-supplied builtins, {predictedCount} predicted localClientNum");
        if ( scoring is not null )
        {
            _output.WriteLine($"    CONTROL — {scoring}");
        }

        foreach ( ArityFinding finding in findings.Take(40) )
        {
            string mark = finding.PredictedLocalClientNum ? "*" : " ";
            string control = finding.ControlAnswer is null ? "" : $"  [known: {finding.ControlAnswer}]";
            _output.WriteLine(
                $" {mark} {finding.FileCount,4} files {finding.Calls,5} calls  {finding.Name}  "
                + $"declared {finding.DeclaredMax}, seen [{string.Join(',', finding.ObservedArgCounts)}]  "
                + $"conf {finding.ClientIndexConfidence:0.00}{control}");
        }

        ArityReport report = new(profile.ShortName, swept, findings.Count, predictedCount, scoring, findings);

        string directory = Path.Combine(FindProjectRoot() ?? AppContext.BaseDirectory, "harvest");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, $"{profile.ShortName}_client_arity.json");

        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            NewLine = "\n",
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        File.WriteAllText(file, json + "\n", new System.Text.UTF8Encoding(false));
        _output.WriteLine($"Report: {file}");
    }

    /// <summary>
    /// Which of the server library's names the shipped client scripts actually call — the evidence for
    /// pruning an inferred client library down to what is known to exist on the client VM.
    ///
    /// BO3 measures the price of doing that. It is the one game where the true client-side answer is
    /// known, so restricting to names its GSC and CSC libraries share gives a set that is certainly
    /// client-side, and the share of THOSE that its stock client scripts happen to call is the
    /// survival rate a corpus-only prune would produce on a game where the answer is not known.
    /// </summary>
    private void WriteObserved(
        GameProfile profile, int swept, Dictionary<string, Observed> observed, BuiltinApi server, BuiltinApi client)
    {
        List<ObservedFunction> functions = [];
        foreach ( KeyValuePair<string, Observed> entry in observed )
        {
            functions.Add(new ObservedFunction(
                entry.Key, entry.Value.Calls, entry.Value.Files.Count, client.Find(entry.Key) is not null));
        }

        functions.Sort(static (left, right) =>
        {
            int byFiles = right.FileCount.CompareTo(left.FileCount);
            return byFiles != 0 ? byFiles : right.Calls.CompareTo(left.Calls);
        });

        string coverage;
        if ( client.Count > 0 )
        {
            int knownClientSide = 0;
            int knownAndCalled = 0;
            foreach ( BuiltinFunction function in server.All )
            {
                if ( client.Find(function.Name) is null )
                {
                    continue;
                }

                knownClientSide++;
                if ( observed.ContainsKey(function.Name) )
                {
                    knownAndCalled++;
                }
            }

            double survival = knownClientSide == 0 ? 0 : (double)knownAndCalled / knownClientSide;
            coverage = $"CONTROL — {knownClientSide} names are in both this game's server and client libraries, "
                + $"so are certainly client-side; its stock client scripts call {knownAndCalled} of them "
                + $"({survival:P1}). A corpus-only prune keeps that share and discards the rest.";
        }
        else
        {
            coverage = $"{observed.Count} of the server library's {server.Count} names appear in client scripts.";
        }

        _output.WriteLine($"    OBSERVED — {coverage}");

        ObservedReport report = new(profile.ShortName, swept, server.Count, functions.Count, coverage, functions);

        string directory = Path.Combine(FindProjectRoot() ?? AppContext.BaseDirectory, "harvest");
        Directory.CreateDirectory(directory);

        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            NewLine = "\n",
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        File.WriteAllText(
            Path.Combine(directory, $"{profile.ShortName}_csc_observed.json"), json + "\n", new System.Text.UTF8Encoding(false));
    }

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
}
