using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Workspace.Analysis;
using Xunit;
using Xunit.Abstractions;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// What the unassigned-variable rule reports on code that SHIPPED and works.
///
/// The rule is a heuristic about names, so its whole risk is false positives, and the corpus is
/// the only place to measure that honestly. Anything it reports here is either a real latent bug
/// in the game's own scripts or a gap in the rule's exclusions — and the second is far more likely.
///
/// This is a MEASUREMENT rather than a gate: it prints what it finds and asserts only that the
/// rate is low enough for the rule to be worth having. A rule reporting thousands of names on
/// working code would be one nobody could use.
/// </summary>
[Trait("Category", "Corpus")]
[Collection(GameProfileCollection.Name)]
public class UnassignedVariableSweepTests
{
    private readonly ITestOutputHelper _output;

    public UnassignedVariableSweepTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void TheRateOnShippedScriptsIsLowEnoughToBeUseful()
    {
        IReadOnlyList<GameCorpus> corpora = GameCorpusFixture.Available();
        if ( corpora.Count == 0 )
        {
            _output.WriteLine("SKIPPED: no per-game corpus configured.");
            return;
        }

        foreach ( GameCorpus corpus in corpora )
        {
            int scripts = 0;
            int reports = 0;
            Dictionary<string, int> byName = new(StringComparer.OrdinalIgnoreCase);

            GSCode.Workspace.Resolution.PathResolver resolver = GameCorpusFixture.Resolver(corpus);
            NameTable names = new();

            foreach ( string path in GameCorpusFixture.Scripts(corpus) )
            {
                ParseResult result;
                try
                {
                    result = GameCorpusFixture.Analyze(corpus, path, resolver, names);
                }
                catch
                {
                    continue;
                }

                scripts++;
                ImmutableArray<Diagnostic> found = UnassignedVariableLint.Analyze(result, corpus.Profile);
                reports += found.Length;

                foreach ( Diagnostic diagnostic in found )
                {
                    string name = diagnostic.Message;
                    byName[name] = byName.GetValueOrDefault(name) + 1;
                }
            }

            double perScript = scripts == 0 ? 0 : (double)reports / scripts;
            _output.WriteLine(
                $"{corpus.Profile.ShortName}: {reports} reports over {scripts} scripts ({perScript:F2} per script).");

            foreach ( KeyValuePair<string, int> entry in byName.OrderByDescending(static e => e.Value).Take(15) )
            {
                _output.WriteLine($"    {entry.Value,6}  {entry.Key}");
            }

            Assert.True(
                perScript < 0.05,
                $"{corpus.Profile.ShortName} reports {perScript:F3} unassigned variables per shipped script — "
                + "measured at 17 across 7,309 scripts when this was written, so a jump means a gap "
                + "in the rule's exclusions rather than a defect rate in the game's code");
        }
    }
}
