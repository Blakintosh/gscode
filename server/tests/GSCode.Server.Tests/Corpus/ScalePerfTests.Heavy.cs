using System.Diagnostics;
using System.Text;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Resolution;
using Newtonsoft.Json;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using LspPosition = OmniSharp.Extensions.LanguageServer.Protocol.Models.Position;
using SymbolKind = GSCode.Core.Symbols.SymbolKind;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// The requests whose ANSWER grows with the workspace, timed where it is largest. The handler sweep
/// samples the first function of a file, which in a stock tree is usually called a handful of times;
/// when one file is duplicated thousands of times (<c>GSCODE_SCALE_SOURCE</c>) every stock function
/// it calls is called once per copy, and these are the requests that pay for it:
///
/// - find-references and rename on the stock function the copies call most, timed through the handler
///   AND serialized, since PERF.md's literal-completion lesson is that the payload costs more than
///   building it and no engine timing sees it;
/// - CodeLens on the stock file declaring that function, which runs the reference query once per
///   declaration just to count, and on one copy;
/// - workspace symbol search, which walks every record with a substring match and no index;
/// - how many diagnostics the index holds for the editor to publish in full mode.
///
/// Runs for every shape; on the mixed one the hot key is simply less hot.
/// </summary>
public partial class ScalePerfTests
{
    private static readonly string[] s_symbolQueries = ["", "a", "zm", "spawn", "init"];

    private sealed class HeavyRow
    {
        public string HotKey { get; set; } = "";
        public int HotCallsInFile { get; set; }
        public string DeclaringFile { get; set; } = "";
        public int ReferenceCount { get; set; }
        public double ReferencesMilliseconds { get; set; }
        public double QueryMilliseconds { get; set; }
        public int QueryResults { get; set; }
        public List<string> DeclaringLensTitles { get; } = [];
        public double SerializeMilliseconds { get; set; }
        public long PayloadChars { get; set; }
        public double RenameMilliseconds { get; set; }
        public int RenameEdits { get; set; }
        public double DeclaringLensMilliseconds { get; set; }
        public int DeclaringLenses { get; set; }
        public double CopyLensMilliseconds { get; set; }
        public int CopyLenses { get; set; }
        public List<(string Key, double Milliseconds, int Results)> SlowestLensKeys { get; } = [];
        public List<(string Query, double Milliseconds, int Results)> Symbols { get; } = [];
        public long Diagnostics { get; set; }
        public int FilesWithDiagnostics { get; set; }
        public long References { get; set; }
    }

    private static async Task<HeavyRow> MeasureHeavyAsync(
        ScaleCorpus corpus, ScriptDatabase database, PathResolver resolver, NameTable names,
        BuiltinApiSet builtins, ObjectFields objectFields)
    {
        HeavyRow row = new();

        foreach ( ScriptRecord record in database.AllRecords )
        {
            row.References += record.References.Length;
            if ( !record.Diagnostics.IsEmpty )
            {
                row.Diagnostics += record.Diagnostics.Length;
                row.FilesWithDiagnostics++;
            }
        }

        WorkspaceSymbolHandler symbols = new(database);
        foreach ( string query in s_symbolQueries )
        {
            WorkspaceSymbolParams request = new() { Query = query };
            Container<WorkspaceSymbol>? found = await symbols.Handle(request, CancellationToken.None);
            double elapsed = await TimeTwiceAsync(() => symbols.Handle(request, CancellationToken.None));
            row.Symbols.Add((query, elapsed, found?.Count() ?? 0));
        }

        List<string> copies = ScriptFilesOnly(ScaleCorpusFixture.CopiedScripts(corpus));
        if ( copies.Count == 0 )
        {
            return row;
        }

        string copy = copies[copies.Count / 2];

        ResolverHolder holder = new(new PhysicalFileSystem()) { Current = resolver };
        InsertCache inserts = new();
        DocumentStore documents = new(
            candidate => new ResolverInsertProvider(resolver, resolver.GetContext(candidate), new PhysicalFileSystem(), inserts),
            names);
        NavigationSupport support = new(documents, database, holder);
        TextDocumentSelector selector = TextDocumentSelector.ForLanguage("gsc");
        ServerSettings settings = new() { CodeLensEnabled = true };

        ReferencesHandler references = new(support, selector);
        RenameHandler rename = new(support, builtins, objectFields, selector);
        DefinitionHandler definition = new(support, selector);
        CodeLensHandler lenses = new(support, settings, selector, LensCountCache.Disabled);

        OpenDocument document = documents.Open(copy, File.ReadAllText(copy), version: 1);
        ParseResult parsed = documents.AnalyzeIfStale(document);
        TextDocumentIdentifier copyIdentifier = new() { Uri = DocumentUri.FromFileSystemPath(copy) };

        CodeLensParams copyLensRequest = new() { TextDocument = copyIdentifier };
        row.CopyLenses = (await lenses.HandleResolvedAsync(copyLensRequest, CancellationToken.None))?.Count() ?? 0;
        row.CopyLensMilliseconds = await TimeTwiceAsync(() => lenses.HandleResolvedAsync(copyLensRequest, CancellationToken.None));
        SlowestLensKeys(row, support, copyIdentifier.Uri, parsed);

        ReferenceEntry? hot = HottestForeignCall(parsed, out int hotCount);
        if ( hot is null )
        {
            documents.Close(copy);
            return row;
        }

        ReferenceEntry hotEntry = hot.Value;
        row.HotKey = $"{hotEntry.Key.Namespace}::{hotEntry.Key.Name}";
        row.HotCallsInFile = hotCount;

        LspPosition at = new(hotEntry.Range.Start.Line, hotEntry.Range.Start.Character);
        ReferenceParams referenceRequest = new()
        {
            TextDocument = copyIdentifier,
            Position = at,
            Context = new ReferenceContext { IncludeDeclaration = true },
        };

        LocationContainer? locations = await references.Handle(referenceRequest, CancellationToken.None);
        row.ReferencesMilliseconds = await TimeTwiceAsync(() => references.Handle(referenceRequest, CancellationToken.None));
        row.ReferenceCount = locations?.Count() ?? 0;

        // The query alone, without the handler turning each hit into an LSP Location: tells a slow
        // walk apart from a slow mapping of a large answer.
        NavigationTarget? copyTarget = support.ResolveFresh(copyIdentifier.Uri, CancellationToken.None);
        if ( copyTarget is not null )
        {
            support.FindAllReferences(copyTarget, hotEntry.Key);
            long queryStarted = Stopwatch.GetTimestamp();
            row.QueryResults = support.FindAllReferences(copyTarget, hotEntry.Key).Length;
            row.QueryMilliseconds = Stopwatch.GetElapsedTime(queryStarted).TotalMilliseconds;
        }

        Stopwatch serialize = Stopwatch.StartNew();
        string payload = JsonConvert.SerializeObject(locations);
        serialize.Stop();
        row.SerializeMilliseconds = serialize.Elapsed.TotalMilliseconds;
        row.PayloadChars = payload.Length;
        payload = null!;

        RenameParams renameRequest = new() { TextDocument = copyIdentifier, Position = at, NewName = "scale_renamed" };
        WorkspaceEdit? edit = await rename.Handle(renameRequest, CancellationToken.None);
        row.RenameMilliseconds = await TimeTwiceAsync(() => rename.Handle(renameRequest, CancellationToken.None));
        row.RenameEdits = CountEdits(edit);

        LocationOrLocationLinks? declared = await definition.Handle(
            new DefinitionParams { TextDocument = copyIdentifier, Position = at }, CancellationToken.None);
        documents.Close(copy);

        string? declaringPath = null;
        foreach ( LocationOrLocationLink link in declared ?? new LocationOrLocationLinks() )
        {
            if ( link.IsLocation && link.Location is not null )
            {
                declaringPath = link.Location.Uri.GetFileSystemPath();
                break;
            }
        }

        if ( declaringPath is null )
        {
            return row;
        }

        row.DeclaringFile = Path.GetRelativePath(corpus.RawRoot, declaringPath);
        documents.Open(declaringPath, File.ReadAllText(declaringPath), version: 1);
        CodeLensParams declaringLensRequest = new() { TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(declaringPath) } };
        CodeLensContainer? declaringLenses = await lenses.HandleResolvedAsync(declaringLensRequest, CancellationToken.None);
        row.DeclaringLenses = declaringLenses?.Count() ?? 0;
        foreach ( CodeLens lens in declaringLenses ?? new CodeLensContainer() )
        {
            row.DeclaringLensTitles.Add($"line {lens.Range.Start.Line + 1}: {lens.Command?.Title}");
        }

        row.DeclaringLensMilliseconds = await TimeTwiceAsync(() => lenses.HandleResolvedAsync(declaringLensRequest, CancellationToken.None));
        documents.Close(declaringPath);

        return row;
    }

    /// <summary>
    /// The copy's CodeLens taken apart: the reference query each function's lens runs, timed one key
    /// at a time, so a lens total that grows with the workspace names the keys doing it — and how
    /// many references each really has, since a slow key with a small answer is a walk, not a payload.
    /// </summary>
    private static void SlowestLensKeys(HeavyRow row, NavigationSupport support, DocumentUri uri, ParseResult parsed)
    {
        NavigationTarget? target = support.ResolveFresh(uri, CancellationToken.None);
        if ( target is null )
        {
            return;
        }

        // The keys CodeLensHandler asks about: every function, and every macro defined in the file.
        List<SymbolKey> keys = [];
        foreach ( FunctionSymbol function in parsed.Extraction.Functions )
        {
            keys.Add(new SymbolKey(GameProfile.Active.KeyNamespace(function.Namespace), function.KeyName, SymbolKind.Function));
        }

        foreach ( GSCode.Parser.Preprocessing.MacroDefinition macro in parsed.Preprocessed.Macros.All )
        {
            if ( macro.SourceFile is null )
            {
                keys.Add(new SymbolKey(null, macro.Name, SymbolKind.Macro));
            }
        }

        List<(string Key, double Milliseconds, int Results)> timings = [];
        foreach ( SymbolKey key in keys )
        {
            support.FindAllReferences(target, key);

            long started = Stopwatch.GetTimestamp();
            int results = support.FindAllReferences(target, key).Length;
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            timings.Add(($"{key.Kind} {key.Namespace}::{key.Name}", elapsed, results));
        }

        timings.Sort(static (left, right) => right.Milliseconds.CompareTo(left.Milliseconds));
        row.SlowestLensKeys.AddRange(timings.Take(5));
    }

    /// <summary>
    /// The qualified call this file makes most often to a function it does not declare itself — the
    /// stock function every copy of it calls, so the one whose reference list grows fastest.
    /// </summary>
    private static ReferenceEntry? HottestForeignCall(ParseResult parsed, out int count)
    {
        // An unqualified call is keyed under the file's own namespace, builtins included, so a call
        // into the file's own namespaces is not a call to a stock function.
        HashSet<string> ownNamespaces = new(StringComparer.OrdinalIgnoreCase);
        foreach ( FunctionSymbol function in parsed.Extraction.Functions )
        {
            ownNamespaces.Add(function.Namespace);
        }

        Dictionary<SymbolKey, (ReferenceEntry First, int Count)> calls = [];
        foreach ( ReferenceEntry entry in parsed.Extraction.References )
        {
            if ( entry.Kind != ReferenceKind.Call || entry.FromMacro || entry.Key.Namespace is null || ownNamespaces.Contains(entry.Key.Namespace) )
            {
                continue;
            }

            if ( calls.TryGetValue(entry.Key, out (ReferenceEntry First, int Count) seen) )
            {
                calls[entry.Key] = (seen.First, seen.Count + 1);
            }
            else
            {
                calls[entry.Key] = (entry, 1);
            }
        }

        ReferenceEntry? best = null;
        count = 0;
        foreach ( (ReferenceEntry First, int Count) candidate in calls.Values )
        {
            if ( candidate.Count > count )
            {
                best = candidate.First;
                count = candidate.Count;
            }
        }

        return best;
    }

    private static int CountEdits(WorkspaceEdit? edit)
    {
        if ( edit is null )
        {
            return 0;
        }

        int total = 0;
        if ( edit.Changes is not null )
        {
            foreach ( IEnumerable<TextEdit> edits in edit.Changes.Values )
            {
                total += edits.Count();
            }
        }

        if ( edit.DocumentChanges is not null )
        {
            foreach ( WorkspaceEditDocumentChange change in edit.DocumentChanges )
            {
                if ( change.IsTextDocumentEdit && change.TextDocumentEdit is not null )
                {
                    total += change.TextDocumentEdit.Edits.Count();
                }
            }
        }

        return total;
    }

    /// <summary>The heavy probes' table for the report, after the lookup table.</summary>
    private static void AppendHeavyTable(StringBuilder table, List<ScaleRow> rows)
    {
        table.AppendLine();
        table.AppendLine("| game | files | KB/file | refs held | diagnostics | hot key | locations | refs ms | serialize ms | payload M chars | rename ms | lens on declaring ms | lens on copy ms | symbol \"\" ms | symbol \"a\" ms |");
        table.AppendLine("|---|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach ( ScaleRow row in rows )
        {
            if ( row.Heavy is null )
            {
                continue;
            }

            HeavyRow heavy = row.Heavy;
            double emptyQuery = heavy.Symbols.Count > 0 ? heavy.Symbols[0].Milliseconds : 0;
            double letterQuery = heavy.Symbols.Count > 1 ? heavy.Symbols[1].Milliseconds : 0;
            table.AppendLine(
                $"| {row.Game} | {row.Total:N0} | {row.RetainedMegabytes * 1024 / Math.Max(1, row.Total):F0} | {heavy.References:N0} | {heavy.Diagnostics:N0} | "
                + $"`{heavy.HotKey}` | {heavy.ReferenceCount:N0} | {heavy.ReferencesMilliseconds:F1} | {heavy.SerializeMilliseconds:F1} | "
                + $"{heavy.PayloadChars / 1048576.0:F1} | {heavy.RenameMilliseconds:F1} | {heavy.DeclaringLensMilliseconds:F1} | {heavy.CopyLensMilliseconds:F1} | "
                + $"{emptyQuery:F1} | {letterQuery:F1} |");
        }

        table.AppendLine();
        AppendLegend(table,
        [
            ("KB/file", "retained managed memory divided by the files indexed"),
            ("refs held / diagnostics", "references every record stores, and the diagnostics full mode would publish"),
            ("hot key", "the qualified call a sampled copy makes most often to a function declared elsewhere"),
            ("locations / refs ms / serialize ms / payload", "find-references on it through the handler, then the answer serialized as JSON"),
            ("lens on declaring / copy", "CodeLens for the whole file declaring the hot key, and for one copy"),
            ("symbol", "workspace symbol search for an empty query and for one letter, each capped at 256 results"),
        ]);
    }

    private void WriteHeavy(HeavyRow heavy)
    {
        _output.WriteLine($"     references held   {heavy.References,12:N0} in the index; {heavy.Diagnostics:N0} diagnostics on {heavy.FilesWithDiagnostics:N0} files to publish");
        _output.WriteLine($"       at {System.Runtime.CompilerServices.Unsafe.SizeOf<ReferenceEntry>()} bytes each, {heavy.References * System.Runtime.CompilerServices.Unsafe.SizeOf<ReferenceEntry>() / 1048576.0:N0} MB of entries alone");
        _output.WriteLine($"     hot key           {heavy.HotKey} ({heavy.HotCallsInFile} calls per copy), declared in {heavy.DeclaringFile}");
        _output.WriteLine($"       references      {heavy.ReferencesMilliseconds,8:F1} ms  {heavy.ReferenceCount:N0} locations; serialize {heavy.SerializeMilliseconds:F1} ms, {heavy.PayloadChars / 1024.0 / 1024.0:F1} M chars");
        _output.WriteLine($"       query alone     {heavy.QueryMilliseconds,8:F1} ms  {heavy.QueryResults:N0} results, before the handler maps them");
        _output.WriteLine($"       rename          {heavy.RenameMilliseconds,8:F1} ms  {heavy.RenameEdits:N0} edits");
        _output.WriteLine($"       codeLens decl.  {heavy.DeclaringLensMilliseconds,8:F1} ms  {heavy.DeclaringLenses} lenses on the declaring file");

        foreach ( string title in heavy.DeclaringLensTitles )
        {
            _output.WriteLine($"         {title}");
        }
        _output.WriteLine($"       codeLens copy   {heavy.CopyLensMilliseconds,8:F1} ms  {heavy.CopyLenses} lenses on one copy");

        foreach ( (string Key, double Milliseconds, int Results) key in heavy.SlowestLensKeys )
        {
            _output.WriteLine($"         lens key {key.Key,-48} {key.Milliseconds,7:F2} ms  {key.Results:N0} references");
        }

        foreach ( (string Query, double Milliseconds, int Results) symbol in heavy.Symbols )
        {
            _output.WriteLine($"       workspace symbol \"{symbol.Query}\" {symbol.Milliseconds,8:F1} ms  {symbol.Results} results");
        }
    }
}
