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

    /// <summary>Indexes the whole corpus, then lints every file against the finished database.</summary>
    private static async Task<List<Finding>> SweepAsync()
    {
        PathResolver resolver = CorpusFixture.Resolver();
        PhysicalFileSystem fileSystem = new();
        NameTable names = new();
        ScriptDatabase database = new();

        WorkspaceIndexer indexer = new(database, () => resolver, fileSystem, names);
        await indexer.IndexAsync(IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None);

        BuiltinApiSet builtins = BuiltinApiSet.Load(ApiDirectory);
        ObjectFields objectFields = ObjectFields.Load(ApiDirectory);

        List<Finding> findings = [];
        foreach ( string path in CorpusFixture.Scripts() )
        {
            ScriptLanguage language = ScriptAnalysis.LanguageFromPath(path);

            try
            {
                GSCode.Parser.ParseResult result = CorpusFixture.Analyze(path, resolver, names);

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
        if ( !CorpusFixture.Available )
        {
            _output.WriteLine("SKIPPED: %TA_TOOLS_PATH%\\share\\raw not found.");
            return;
        }

        List<Finding> findings = await SweepAsync();

        WriteReport(findings);

        _output.WriteLine($"{findings.Count} diagnostics across the stock corpus.");
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
            _output.WriteLine("SKIPPED: %TA_TOOLS_PATH%\\share\\raw not found.");
            return;
        }

        List<Finding> findings = await SweepAsync();

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
            _output.WriteLine("SKIPPED: %TA_TOOLS_PATH%\\share\\raw not found.");
            return;
        }

        List<Finding> findings = await SweepAsync();

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
    /// Writes the HTML report. Defaults to the user's temp folder rather than the repository: it
    /// is a snapshot of whatever mod-tools install is on this machine, not a build artifact.
    /// Set GSCODE_SWEEP_REPORT to put it somewhere else.
    /// </summary>
    private void WriteReport(List<Finding> findings)
    {
        string path = Environment.GetEnvironmentVariable("GSCODE_SWEEP_REPORT") is string configured
            && configured.Length > 0
                ? configured
                : Path.Combine(Path.GetTempPath(), "gscode-corpus-sweep.html");

        SweepReport.Write(
            path,
            [.. findings.Select(f => new SweepReport.Item(f.Code, f.Severity, f.Message, f.Path, f.Line, f.Character))],
            CorpusFixture.RawRoot ?? "");

        _output.WriteLine($"Report: {path}");
    }
}
