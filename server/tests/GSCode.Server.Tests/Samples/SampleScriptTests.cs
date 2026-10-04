using System.Text;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Server.Tests.Corpus;
using Xunit;
using Xunit.Abstractions;

namespace GSCode.Server.Tests.Samples;

/// <summary>
/// The gate on <c>server/samples</c> — a hand-written script per game per language world, showing
/// the whole surface of the dialect and of the diagnostics, checked against its own
/// <c>// expect</c> comments.
///
/// The files exist to be OPENED: they are what a contributor loads to see hover, completion,
/// go-to-definition, semantic tokens and every rule firing on one page, and what a screenshot of the
/// extension is taken from. A demo file with nothing asserting its output stops being true within
/// two releases and nobody notices, so the same files are the golden corpus here — one artifact,
/// checked, rather than a demo and a fixture that drift apart.
///
/// Three roles per world, because errors and demonstration fight each other:
/// <list type="bullet">
/// <item><c>gscode.*</c> — the showcase. Must produce ZERO diagnostics, so it stays the file where
/// "does hover still work" is a fair question.</item>
/// <item><c>gscode_lints.*</c> — one deliberate 4000/5000-range finding at a time. It still parses
/// cleanly, so every rule is judged against a complete tree.</item>
/// <item><c>gscode_broken.*</c> — the 1000/2000/3000 ranges. Kept apart because a syntax error puts
/// the parser into recovery and everything below it is analysed degraded; mixed in with the lints,
/// half of them would silently stop being tested.</item>
/// </list>
/// <c>gscode_target.*</c> is the second file the cross-file rules need to have an opinion at all.
///
/// Joins <c>GameProfileCollection</c>: <c>SampleWorkspace.AnalyzeAsync</c> moves <c>GameProfile.Active</c>,
/// and a game analysed under another's dialect fails in ways that look like the rule under test.
/// </summary>
[Collection(GameProfileCollection.Name)]
public class SampleScriptTests
{
    private readonly ITestOutputHelper _output;

    public SampleScriptTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static TheoryData<string> Games()
    {
        TheoryData<string> data = [];
        foreach ( GameProfile profile in SampleWorkspace.Games() )
        {
            data.Add(profile.ShortName);
        }

        return data;
    }

    /// <summary>
    /// Every diagnostic a sample produces is one it asked for, and every one it asked for is
    /// produced.
    ///
    /// Both halves matter and for different reasons. A missing diagnostic is a rule that regressed.
    /// An EXTRA one is the more valuable half: the showcase files expect nothing at all, so any
    /// finding there is a false positive caught on code that was written to be correct — which is
    /// the same bug-finding the corpus sweep does, on scripts anyone can read without owning the
    /// game.
    /// </summary>
    [Theory]
    [MemberData(nameof(Games))]
    public async Task Samples_ProduceExactlyTheDiagnosticsTheyDeclare(string game)
    {
        GameProfile profile = GameProfile.ByName(game) ?? throw new InvalidOperationException(game);

        StringBuilder failures = new();

        foreach ( SampleFile file in await SampleWorkspace.AnalyzeAsync(profile) )
        {
            IReadOnlyList<SampleExpectation> expectations = SampleExpectations.Parse(file.Text);

            // Anchored expectations are matched on (line, code); "anywhere" ones on the code alone.
            // Both are multisets, so two of the same code on one line must be written twice.
            List<(GscDiagnosticCode Code, int Line)> outstanding =
            [
                .. expectations.Where(static e => !e.Anywhere).Select(static e => (e.Code, e.Line)),
            ];
            List<GscDiagnosticCode> anywhere = [.. expectations.Where(static e => e.Anywhere).Select(static e => e.Code)];

            List<Diagnostic> unexpected = [];

            foreach ( Diagnostic diagnostic in file.Diagnostics )
            {
                int index = outstanding.IndexOf((diagnostic.Code, diagnostic.Range.Start.Line));
                if ( index >= 0 )
                {
                    outstanding.RemoveAt(index);
                    continue;
                }

                int loose = anywhere.IndexOf(diagnostic.Code);
                if ( loose >= 0 )
                {
                    anywhere.RemoveAt(loose);
                    continue;
                }

                unexpected.Add(diagnostic);
            }

            foreach ( Diagnostic diagnostic in unexpected )
            {
                failures.AppendLine(
                    $"{file.RelativePath}:{diagnostic.Range.Start.Line + 1}  UNEXPECTED {(int)diagnostic.Code} " +
                    $"{diagnostic.Code} — {diagnostic.Message}");
            }

            foreach ( (GscDiagnosticCode code, int line) in outstanding )
            {
                failures.AppendLine($"{file.RelativePath}:{line + 1}  MISSING {(int)code} {code}");
            }

            foreach ( GscDiagnosticCode code in anywhere )
            {
                failures.AppendLine($"{file.RelativePath}  MISSING (anywhere) {(int)code} {code}");
            }
        }

        if ( failures.Length > 0 )
        {
            _output.WriteLine(failures.ToString());
        }

        Assert.True(failures.Length == 0, $"{game} samples disagree with their // expect comments:\n{failures}");
    }

    /// <summary>
    /// The showcase file of every world a game HAS is present, and no world it lacks has one.
    ///
    /// This is the half a per-file test cannot see. A missing <c>gscode.csc</c> under BO1 is not a
    /// failing assertion anywhere — the file is simply never enumerated, and a suite that only
    /// checks the files it finds reports green on an empty folder. The profile's own capability
    /// flags are the specification: <c>HasClientScripts</c> decides whether a <c>.csc</c> is owed,
    /// <c>HasHeaders</c> whether a <c>.gsh</c> is.
    /// </summary>
    [Theory]
    [MemberData(nameof(Games))]
    public void EveryLanguageWorldTheGameHasIsSampled(string game)
    {
        GameProfile profile = GameProfile.ByName(game) ?? throw new InvalidOperationException(game);
        string root = SampleWorkspace.RootFor(profile)!;

        foreach ( string extension in profile.ScriptExtensions )
        {
            string[] showcases = Directory.GetFiles(root, "gscode" + extension, SearchOption.AllDirectories);
            Assert.True(
                showcases.Length == 1,
                $"{game} has {extension} scripts, so exactly one showcase 'gscode{extension}' is owed " +
                $"under server/samples/{game}; found {showcases.Length}.");
        }

        // The other direction: a world the game does not have must not be sampled, or the sample
        // claims a capability the profile denies.
        foreach ( string extension in new[] { ".gsc", ".csc", ".gsh" } )
        {
            if ( profile.ScriptExtensions.Contains(extension) )
            {
                continue;
            }

            string[] strays = Directory.GetFiles(root, "*" + extension, SearchOption.AllDirectories);
            Assert.True(
                strays.Length == 0,
                $"{game} has no {extension} world, but server/samples/{game} contains " +
                $"{string.Join(", ", strays.Select(Path.GetFileName))}.");
        }
    }
}
