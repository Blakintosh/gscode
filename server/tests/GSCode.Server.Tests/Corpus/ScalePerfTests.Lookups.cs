using System.Collections.Immutable;
using System.Diagnostics;
using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Workspace.Completion;
using GSCode.Workspace.Database;
using GSCode.Workspace.Resolution;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// The queries that still read a broad list and filter it, timed at scale so an index that narrows
/// one has a before and an after: path completion inside a directive, the files a changed header
/// re-indexes, a file rename's directive edits, the two class queries, macro references in headers,
/// and <c>ArgumentCountLint</c> on its own.
///
/// None of these was on the scale table before. Path completion is per keystroke; the rest are per
/// request or per watched-file event, so they are reported beside the others rather than budgeted.
/// </summary>
public partial class ScalePerfTests
{
    private const int LookupSampleFiles = 100;

    private const int HeaderSampleFiles = 40;

    private const int ClassSampleFiles = 50;

    private sealed class Distribution
    {
        public List<double> Times { get; } = [];

        public double P99
        {
            get { return Percentile(Sorted(), 0.99); }
        }

        public double Max
        {
            get { return Times.Count == 0 ? 0 : Times.Max(); }
        }

        public int Count
        {
            get { return Times.Count; }
        }

        private List<double> Sorted()
        {
            List<double> sorted = [.. Times];
            sorted.Sort();
            return sorted;
        }

        public string Describe()
        {
            return $"p99 {P99,8:F2} ms  max {Max,7:F2} ms  ({Count} requests)";
        }
    }

    private sealed class LookupRow
    {
        public Distribution ImportPath { get; } = new();
        public Distribution InsertPath { get; } = new();
        public Distribution HeaderInserters { get; } = new();
        public Distribution RenamePlan { get; } = new();
        public Distribution ClassLookup { get; } = new();
        public Distribution VisibleClasses { get; } = new();
        public Distribution HeaderMacroReferences { get; } = new();
        public int InsertersFound { get; set; }
        public int RenameEdits { get; set; }
    }

    private static LookupRow MeasureLookups(
        ScaleCorpus corpus, ScriptDatabase database, PathResolver resolver, NameTable names, InsertCache inserts,
        CompletionEngine engine)
    {
        LookupRow lookups = new();

        List<string> stock = ScaleCorpusFixture.StockScripts(corpus);
        List<string> sample = [];
        sample.AddRange(Sample(ScriptFilesOnly(stock), LookupSampleFiles / 2));
        sample.AddRange(Sample(ScriptFilesOnly(ScaleCorpusFixture.CopiedScripts(corpus)), LookupSampleFiles / 2));

        foreach ( string path in sample )
        {
            string text;
            ParseResult parsed;
            try
            {
                text = File.ReadAllText(path);
                parsed = ScriptAnalysis.Analyze(
                    path,
                    corpus.Profile.LanguageFromPath(path),
                    SourceText.From(text),
                    new ResolverInsertProvider(resolver, resolver.GetContext(path), new PhysicalFileSystem(), inserts),
                    names,
                    corpus.Profile,
                    inserts);
            }
            catch ( Exception )
            {
                continue;
            }

            string contextId = ScriptDatabase.ContextIdOf(resolver.GetContext(path));
            LanguageStore store = database.StoreFor(ScriptAnalysis.LanguageFromPath(path));

            Position? importAt = DirectivePathPosition(text, corpus.Profile.ResolvesByNamespace ? "#using" : "#include");
            if ( importAt is not null )
            {
                lookups.ImportPath.Times.Add(TimeCompletion(engine, parsed, contextId, importAt.Value, corpus.Profile));
            }

            Position? insertAt = DirectivePathPosition(text, "#insert");
            if ( insertAt is not null )
            {
                lookups.InsertPath.Times.Add(TimeCompletion(engine, parsed, contextId, insertAt.Value, corpus.Profile));
            }

            ImmutableArray<string> imported = DatabaseQueries.ImportedScriptPaths(parsed);
            lookups.VisibleClasses.Times.Add(TimeTwice(() => DatabaseQueries.AllVisibleClasses(store, contextId, path, imported)));

            foreach ( ReferenceEntry entry in parsed.Extraction.References )
            {
                if ( entry.Key.Kind != SymbolKind.Macro )
                {
                    continue;
                }

                SymbolKey key = entry.Key;
                lookups.HeaderMacroReferences.Times.Add(TimeTwice(() => DatabaseQueries.FindGshReferences(database, contextId, key)));
                break;
            }
        }

        MeasureHeaders(lookups, stock, database);
        MeasureRenames(lookups, stock, database, corpus.Profile);
        MeasureClasses(lookups, database);

        return lookups;
    }

    /// <summary>The files a changed header re-indexes, for a sample of the stock headers every copy inserts.</summary>
    private static void MeasureHeaders(LookupRow lookups, List<string> stock, ScriptDatabase database)
    {
        List<string> headers = [];
        foreach ( string path in stock )
        {
            if ( ScriptAnalysis.LanguageFromPath(path) == ScriptLanguage.Gsh )
            {
                headers.Add(PathUtil.NormalizeAbsolute(path));
            }
        }

        foreach ( string header in Sample(headers, HeaderSampleFiles) )
        {
            if ( !database.TryGetGsh(header, out ScriptRecord record) )
            {
                continue;
            }

            string relative = PathUtil.NormalizeScriptPath(record.RelativePath);
            lookups.HeaderInserters.Times.Add(TimeTwice(() => DatabaseQueries.ScriptsInserting(database, header, relative)));
            lookups.InsertersFound += DatabaseQueries.ScriptsInserting(database, header, relative).Count;
        }
    }

    /// <summary>A file rename's directive edits, for stock scripts (imported by every copy) and stock headers.</summary>
    private static void MeasureRenames(LookupRow lookups, List<string> stock, ScriptDatabase database, GameProfile profile)
    {
        List<string> scripts = Sample(ScriptFilesOnly(stock), HeaderSampleFiles);
        List<string> headers = [];
        foreach ( string path in stock )
        {
            if ( ScriptAnalysis.LanguageFromPath(path) == ScriptLanguage.Gsh )
            {
                headers.Add(path);
            }
        }

        foreach ( string path in scripts.Concat(Sample(headers, HeaderSampleFiles)) )
        {
            if ( !database.TryGetAnyRecord(path, out ScriptRecord record) || record.RelativePath.Length == 0 )
            {
                continue;
            }

            bool isInsert = record.Language == ScriptLanguage.Gsh;
            string from = DependencyRewrite.ToDirectivePath(record.RelativePath, isInsert);
            string to = DependencyRewrite.ToDirectivePath("scale_renamed\\" + record.RelativePath, isInsert);

            lookups.RenamePlan.Times.Add(TimeTwice(() => DependencyRewrite.PlanRename(database, from, to, isInsert)));
            lookups.RenameEdits += DependencyRewrite.PlanRename(database, from, to, isInsert).Length;
        }
    }

    /// <summary>Every class declared in a sample of the class-declaring files, looked up as a qualified parent link is.</summary>
    private static void MeasureClasses(LookupRow lookups, ScriptDatabase database)
    {
        foreach ( LanguageStore store in database.BothLanguageStores )
        {
            List<string> declaring = [.. store.Classes.AllDeclaringPaths()];
            declaring.Sort(StringComparer.Ordinal);

            foreach ( string path in Sample(declaring, ClassSampleFiles) )
            {
                if ( !store.TryGet(path, out ScriptRecord record) )
                {
                    continue;
                }

                foreach ( ClassSymbol classSymbol in record.Classes )
                {
                    string contextId = record.ContextId;
                    lookups.ClassLookup.Times.Add(TimeTwice(
                        () => DatabaseQueries.LookupClasses(store, contextId, classSymbol.Namespace, classSymbol.KeyName)));
                }
            }
        }
    }

    /// <summary>
    /// Just after the last separator of the first directive of this kind — <c>#using scripts\shared\|</c> —
    /// where path completion lists one folder. Found in the text, since an <c>#insert</c> never
    /// reaches the tree.
    /// </summary>
    private static Position? DirectivePathPosition(string text, string directive)
    {
        string[] lines = text.Split('\n');
        for ( int line = 0; line < lines.Length; line++ )
        {
            string content = lines[line].TrimEnd('\r');
            if ( !content.StartsWith(directive + " ", StringComparison.Ordinal) )
            {
                continue;
            }

            int separator = content.LastIndexOfAny(['\\', '/']);
            int character = separator >= 0 ? separator + 1 : directive.Length + 1;
            return new Position(line, character);
        }

        return null;
    }

    /// <summary>Runs once to warm, then times a second call.</summary>
    private static double TimeTwice<T>(Func<T> request)
    {
        request();

        long started = Stopwatch.GetTimestamp();
        request();
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private void WriteLookups(LookupRow lookups, ScaleRow row)
    {
        _output.WriteLine($"     import path compl {lookups.ImportPath.Describe()}");
        _output.WriteLine($"     insert path compl {lookups.InsertPath.Describe()}");
        _output.WriteLine($"     header inserters  {lookups.HeaderInserters.Describe()}  {lookups.InsertersFound:N0} files found in all");
        _output.WriteLine($"     rename plan       {lookups.RenamePlan.Describe()}  {lookups.RenameEdits:N0} edits in all");
        _output.WriteLine($"     class lookup      {lookups.ClassLookup.Describe()}");
        _output.WriteLine($"     visible classes   {lookups.VisibleClasses.Describe()}");
        _output.WriteLine($"     header macro refs {lookups.HeaderMacroReferences.Describe()}");

        foreach ( (string Rule, double Max, double P99) rule in row.LintRules )
        {
            if ( rule.Rule.Contains("ArgumentCount", StringComparison.Ordinal) )
            {
                _output.WriteLine($"     ArgumentCountLint p99 {rule.P99,7:F2} ms  max {rule.Max,7:F2} ms");
            }
        }
    }
}
