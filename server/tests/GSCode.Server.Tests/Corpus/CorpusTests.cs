using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Lexing;
using GSCode.Server.Formatting;
using GSCode.Workspace.Resolution;
using Xunit;
using Xunit.Abstractions;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// The real-corpus category: lex/parse every stock script under %TA_TOOLS_PATH%\share\raw, and
/// run the formatter's two property gates over a sample of it. No-ops wherever the corpus is
/// absent (CI, and any machine without the mod tools) — each test reports which branch it took.
/// </summary>
[Trait("Category", "Corpus")]
public class CorpusTests
{
    private readonly ITestOutputHelper _output;

    public CorpusTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private bool SkipWithoutCorpus()
    {
        if ( CorpusFixture.Available )
        {
            return false;
        }

        _output.WriteLine("SKIPPED: %TA_TOOLS_PATH%\\share\\raw not found — corpus tests need a local mod-tools install.");
        return true;
    }

    [Fact]
    public void EveryScript_AnalysesWithoutThrowing()
    {
        if ( SkipWithoutCorpus() )
        {
            return;
        }

        IReadOnlyList<string> scripts = CorpusFixture.Scripts();
        PathResolver resolver = CorpusFixture.Resolver();
        NameTable names = new();

        // The pipeline reports problems as diagnostics and never throws — that is the contract
        // being asserted. Failures name their file, since a stack trace alone would not.
        List<string> failures = [];
        foreach ( string path in scripts )
        {
            try
            {
                CorpusFixture.Analyze(path, resolver, names);
            }
            catch ( Exception exception )
            {
                failures.Add($"{path}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        _output.WriteLine($"Analysed {scripts.Count} scripts.");
        foreach ( string failure in failures.Take(25) )
        {
            _output.WriteLine("  " + failure);
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void ParseErrors_StayWithinBudget()
    {
        if ( SkipWithoutCorpus() )
        {
            return;
        }

        IReadOnlyList<string> scripts = CorpusFixture.Scripts();
        PathResolver resolver = CorpusFixture.Resolver();
        NameTable names = new();

        List<string> withParseErrors = [];
        foreach ( string path in scripts )
        {
            ParseResult result = CorpusFixture.Analyze(path, resolver, names);
            foreach ( Diagnostic diagnostic in result.AllDiagnostics )
            {
                // 1xxx lexing and 3xxx parsing mean the grammar rejected real shipped code.
                int code = (int)diagnostic.Code;
                if ( diagnostic.Severity == DiagnosticSeverity.Error && (code < 2000 || (code >= 3000 && code < 4000)) )
                {
                    withParseErrors.Add($"{path}({diagnostic.Range.Start.Line + 1}): {diagnostic.Code} {diagnostic.Message}");
                    break;
                }
            }
        }

        _output.WriteLine($"{withParseErrors.Count} of {scripts.Count} scripts have lex/parse errors.");
        foreach ( string failure in withParseErrors.Take(25) )
        {
            _output.WriteLine("  " + failure);
        }

        // Stock scripts are valid GSC by definition, so any lex/parse error is a grammar gap on
        // our side. Budgeted rather than zero only so one odd file cannot block the suite; the
        // listing above is the actual signal.
        double failureRate = scripts.Count == 0 ? 0 : (double)withParseErrors.Count / scripts.Count;
        Assert.True(failureRate < 0.01, $"{failureRate:P2} of stock scripts fail to parse — see the list above.");
    }

    [Fact]
    public void Formatter_PreservesTheTokenStream()
    {
        if ( SkipWithoutCorpus() )
        {
            return;
        }

        List<string> violations = [];
        int formatted = ForEachFormattableSample((path, result, output) =>
        {
            ImmutableArray<Token> before = SignificantTokens(result.Lexed.Tokens);
            ImmutableArray<Token> after = SignificantTokens(Lexer.Lex(SourceText.From(output)).Tokens);

            if ( !SameKinds(before, after) )
            {
                violations.Add(path);
            }
        });

        _output.WriteLine($"Checked token-stream equality across {formatted} formatted scripts.");
        foreach ( string violation in violations.Take(25) )
        {
            _output.WriteLine("  " + violation);
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Formatter_IsIdempotent()
    {
        if ( SkipWithoutCorpus() )
        {
            return;
        }

        List<string> violations = [];
        int formatted = ForEachFormattableSample((path, result, output) =>
        {
            ParseResult reparsed = ScriptAnalysis.Analyze(
                path,
                result.Language,
                SourceText.From(output),
                GSCode.Parser.Preprocessing.NullInsertProvider.Instance,
                new NameTable());

            string? second = GscFormatter.Format(reparsed);
            if ( second is not null && !string.Equals(second, output, StringComparison.Ordinal) )
            {
                violations.Add(path);
            }
        });

        _output.WriteLine($"Checked idempotence across {formatted} formatted scripts.");
        foreach ( string violation in violations.Take(25) )
        {
            _output.WriteLine("  " + violation);
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// Formats a bounded sample of the corpus and hands each result to <paramref name="check"/>.
    /// Files the formatter refuses (syntax errors) are skipped rather than counted, since
    /// refusing is the correct behaviour there and already has its own coverage.
    /// </summary>
    private static int ForEachFormattableSample(Action<string, ParseResult, string> check)
    {
        PathResolver resolver = CorpusFixture.Resolver();
        NameTable names = new();
        int formatted = 0;

        foreach ( string path in CorpusFixture.Scripts() )
        {
            if ( formatted >= CorpusFixture.FormatterSampleSize )
            {
                break;
            }

            ParseResult result = CorpusFixture.Analyze(path, resolver, names);
            string? output = GscFormatter.Format(result);
            if ( output is null )
            {
                continue;
            }

            check(path, result, output);
            formatted++;
        }

        return formatted;
    }

    private static ImmutableArray<Token> SignificantTokens(ImmutableArray<Token> tokens)
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
