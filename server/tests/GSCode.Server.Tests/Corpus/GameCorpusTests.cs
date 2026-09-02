using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Lexing;
using GSCode.Server.Formatting;
using GSCode.Workspace.Resolution;
using Xunit;
using Xunit.Abstractions;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// The per-game corpus gate: every supported game's real scripts, lexed and parsed with that game's
/// own profile. This is what <see cref="GameProfile.Verified"/> means — the dialect is not "filled in
/// from a worksheet" but proven against thousands of the game's own shipped scripts.
///
/// No-ops per game when its corpus is not configured (see <see cref="GameCorpusFixture"/>), and each
/// test reports which branch it took.
/// </summary>
[Trait("Category", "Corpus")]
[Collection(GameProfileCollection.Name)]
public class GameCorpusTests
{
    private readonly ITestOutputHelper _output;

    public GameCorpusTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>A lex (1xxx) or parse (3xxx) error means the grammar rejected real shipped code.</summary>
    private static bool IsGrammarError(Diagnostic diagnostic)
    {
        int code = (int)diagnostic.Code;
        return diagnostic.Severity == DiagnosticSeverity.Error && (code < 2000 || (code >= 3000 && code < 4000));
    }

    [Fact]
    public void EveryGamesScripts_AnalyseWithoutThrowing()
    {
        IReadOnlyList<GameCorpus> corpora = GameCorpusFixture.Available();
        if ( corpora.Count == 0 )
        {
            _output.WriteLine("SKIPPED: no per-game corpora configured (set GSCODE_CORPUS_<GAME>).");
            return;
        }

        List<string> failures = [];
        foreach ( GameCorpus corpus in corpora )
        {
            IReadOnlyList<string> scripts = GameCorpusFixture.Scripts(corpus);
            PathResolver resolver = GameCorpusFixture.Resolver(corpus);
            NameTable names = new();

            foreach ( string path in scripts )
            {
                try
                {
                    GameCorpusFixture.Analyze(corpus, path, resolver, names);
                }
                catch ( Exception exception )
                {
                    failures.Add($"[{corpus.Profile.ShortName}] {path}: {exception.GetType().Name}: {exception.Message}");
                }
            }

            _output.WriteLine($"{corpus.Profile.ShortName}: analysed {scripts.Count} scripts.");
        }

        foreach ( string failure in failures.Take(25) )
        {
            _output.WriteLine("  " + failure);
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void EveryGamesScripts_ParseWithinBudget()
    {
        IReadOnlyList<GameCorpus> corpora = GameCorpusFixture.Available();
        if ( corpora.Count == 0 )
        {
            _output.WriteLine("SKIPPED: no per-game corpora configured (set GSCODE_CORPUS_<GAME>).");
            return;
        }

        List<string> overBudget = [];
        foreach ( GameCorpus corpus in corpora )
        {
            IReadOnlyList<string> scripts = GameCorpusFixture.Scripts(corpus);
            PathResolver resolver = GameCorpusFixture.Resolver(corpus);
            NameTable names = new();

            List<string> failing = [];
            Dictionary<GscDiagnosticCode, int> byCode = [];

            foreach ( string path in scripts )
            {
                ParseResult result = GameCorpusFixture.Analyze(corpus, path, resolver, names);
                bool counted = false;
                foreach ( Diagnostic diagnostic in result.AllDiagnostics )
                {
                    if ( !IsGrammarError(diagnostic) )
                    {
                        continue;
                    }

                    byCode[diagnostic.Code] = byCode.GetValueOrDefault(diagnostic.Code) + 1;
                    if ( !counted )
                    {
                        counted = true;
                        failing.Add($"{path}({diagnostic.Range.Start.Line + 1}): {diagnostic.Code} {diagnostic.Message}");
                    }
                }
            }

            double rate = scripts.Count == 0 ? 0 : (double)failing.Count / scripts.Count;
            _output.WriteLine($"{corpus.Profile.ShortName}: {failing.Count} of {scripts.Count} scripts have lex/parse errors ({rate:P2}).");
            foreach ( KeyValuePair<GscDiagnosticCode, int> entry in byCode.OrderByDescending(static e => e.Value) )
            {
                _output.WriteLine($"    {entry.Key} x{entry.Value}");
            }

            foreach ( string failure in failing.Take(20) )
            {
                _output.WriteLine("      " + failure);
            }

            // Same budget the BO3 gate uses: stock scripts are valid by definition, so anything here
            // is a gap on our side; the budget only stops one odd file blocking the suite.
            if ( rate >= 0.01 )
            {
                overBudget.Add($"{corpus.Profile.ShortName}: {rate:P2} of scripts fail to parse");
            }
        }

        Assert.Empty(overBudget);
    }

    /// <summary>
    /// The formatter's two property gates, per game. Parsing a dialect is only half of supporting it —
    /// the formatter must also round-trip its scripts, which is where a dialect-specific token (an
    /// anim reference, a path call, <c>call [[ ]]</c>) would show up as a dropped or invented token.
    /// Reflow must change nothing but whitespace, and formatting must be a fixed point.
    /// </summary>
    [Fact]
    public void EveryGamesScripts_SurviveTheFormatter()
    {
        IReadOnlyList<GameCorpus> corpora = GameCorpusFixture.Available();
        if ( corpora.Count == 0 )
        {
            _output.WriteLine("SKIPPED: no per-game corpora configured (set GSCODE_CORPUS_<GAME>).");
            return;
        }

        List<string> tokenViolations = [];
        List<string> idempotenceViolations = [];

        // Directive sorting moves lines on purpose, so it is off for the token-equality property —
        // matching how the BO3 gate isolates reflow.
        FormatOptions options = FormatOptions.Default with { SortDirectives = false };

        foreach ( GameCorpus corpus in corpora )
        {
            PathResolver resolver = GameCorpusFixture.Resolver(corpus);
            NameTable names = new();
            int formatted = 0;

            foreach ( string path in GameCorpusFixture.Scripts(corpus) )
            {
                if ( formatted >= CorpusFixture.FormatterSampleSize )
                {
                    break;
                }

                ParseResult result = GameCorpusFixture.Analyze(corpus, path, resolver, names);
                string? output = GscFormatter.Format(result, options);
                if ( output is null )
                {
                    // The formatter refuses files with syntax errors, which is correct behaviour.
                    continue;
                }

                formatted++;

                // Re-lex with THIS game's profile: a dialect's keywords differ, so lexing the output
                // as BO3 would compare two different token streams and prove nothing.
                ImmutableArray<Token> before = Significant(result.Lexed.Tokens);
                ImmutableArray<Token> after = Significant(Lexer.Lex(SourceText.From(output), corpus.Profile).Tokens);
                if ( !SameKinds(before, after) )
                {
                    tokenViolations.Add($"[{corpus.Profile.ShortName}] {path}");
                }

                ParseResult reparsed = ScriptAnalysis.Analyze(
                    path,
                    corpus.Profile.LanguageFromPath(path),
                    SourceText.From(output),
                    GSCode.Parser.Preprocessing.NullInsertProvider.Instance,
                    new NameTable(),
                    corpus.Profile);

                string? second = GscFormatter.Format(reparsed, options);
                if ( second is not null && !string.Equals(second, output, StringComparison.Ordinal) )
                {
                    idempotenceViolations.Add($"[{corpus.Profile.ShortName}] {path}");
                }
            }

            _output.WriteLine($"{corpus.Profile.ShortName}: formatted {formatted} scripts.");
        }

        foreach ( string violation in tokenViolations.Take(15) )
        {
            _output.WriteLine("  token change: " + violation);
        }

        foreach ( string violation in idempotenceViolations.Take(15) )
        {
            _output.WriteLine("  not idempotent: " + violation);
        }

        Assert.Empty(tokenViolations);
        Assert.Empty(idempotenceViolations);
    }

    private static ImmutableArray<Token> Significant(ImmutableArray<Token> tokens)
    {
        ImmutableArray<Token>.Builder kept = ImmutableArray.CreateBuilder<Token>();
        foreach ( Token token in tokens )
        {
            if ( !token.IsTrivia )
            {
                kept.Add(token);
            }
        }

        return kept.ToImmutable();
    }

    private static bool SameKinds(ImmutableArray<Token> left, ImmutableArray<Token> right)
    {
        if ( left.Length != right.Length )
        {
            return false;
        }

        for ( int index = 0; index < left.Length; index++ )
        {
            if ( left[index].Kind != right[index].Kind )
            {
                return false;
            }
        }

        return true;
    }
}
