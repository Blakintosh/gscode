using System.Diagnostics;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Resolution;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using LspPosition = OmniSharp.Extensions.LanguageServer.Protocol.Models.Position;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// The navigation handlers at scale: CodeLens, find-references and rename. All three are built on
/// the shared reference query, which reads every file mentioning a key — and in a large workspace a
/// function in a shared utility file is mentioned by thousands. CodeLens pays that once per
/// declaration in the file, on every open and every edit.
///
/// Sampled half from the stock raw scripts and half from the copies: the stock files are the ones
/// every copy imports, so they carry the heaviest reference counts, and an even sample of a 50,000-file
/// workspace would land on them 2% of the time.
/// </summary>
public partial class ScalePerfTests
{
    private const int HandlerSampleFilesPerHalf = 50;

    private static async Task MeasureHandlersAsync(
        ScaleRow row, ScaleCorpus corpus, ScriptDatabase database, PathResolver resolver, NameTable names,
        BuiltinApiSet builtins, ObjectFields objectFields)
    {
        List<string> sample = [];
        sample.AddRange(Sample(ScriptFilesOnly(ScaleCorpusFixture.StockScripts(corpus)), HandlerSampleFilesPerHalf));
        sample.AddRange(Sample(ScriptFilesOnly(ScaleCorpusFixture.CopiedScripts(corpus)), HandlerSampleFilesPerHalf));

        ResolverHolder holder = new(new PhysicalFileSystem()) { Current = resolver };
        InsertCache inserts = new();
        DocumentStore documents = new(
            candidate => new ResolverInsertProvider(resolver, resolver.GetContext(candidate), new PhysicalFileSystem(), inserts),
            names);
        NavigationSupport support = new(documents, database, holder);
        TextDocumentSelector selector = TextDocumentSelector.ForLanguage("gsc");
        ServerSettings settings = new() { CodeLensEnabled = true };

        CodeLensHandler lenses = new(support, settings, selector);
        ReferencesHandler references = new(support, selector);
        RenameHandler rename = new(support, builtins, objectFields, selector);

        List<double> lensTimes = [];
        List<double> referenceTimes = [];
        List<double> renameTimes = [];

        foreach ( string path in sample )
        {
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch ( IOException )
            {
                continue;
            }

            OpenDocument document = documents.Open(path, text, version: 1);
            ParseResult parsed = documents.AnalyzeIfStale(document);
            TextDocumentIdentifier identifier = new() { Uri = DocumentUri.FromFileSystemPath(path) };

            lensTimes.Add(await TimeTwiceAsync(() => lenses.Handle(new CodeLensParams { TextDocument = identifier }, CancellationToken.None)));

            if ( parsed.Extraction.Functions.Length > 0 )
            {
                FunctionSymbol first = parsed.Extraction.Functions[0];
                LspPosition at = new(first.NameRange.Start.Line, first.NameRange.Start.Character);

                ReferenceParams referenceRequest = new()
                {
                    TextDocument = identifier,
                    Position = at,
                    Context = new ReferenceContext { IncludeDeclaration = true },
                };
                referenceTimes.Add(await TimeTwiceAsync(() => references.Handle(referenceRequest, CancellationToken.None)));

                // What came back, so a fast answer cannot hide an empty one. With the declaration
                // included, every sampled function yields at least one location.
                LocationContainer? locations = await references.Handle(referenceRequest, CancellationToken.None);
                row.ReferenceResults += locations?.Count() ?? 0;

                renameTimes.Add(await TimeTwiceAsync(() => rename.Handle(
                    new RenameParams { TextDocument = identifier, Position = at, NewName = "scale_renamed" },
                    CancellationToken.None)));
            }

            documents.Close(path);
        }

        lensTimes.Sort();
        referenceTimes.Sort();
        renameTimes.Sort();

        row.LensP99 = Percentile(lensTimes, 0.99);
        row.LensMax = lensTimes.Count == 0 ? 0 : lensTimes[^1];
        row.ReferencesP99 = Percentile(referenceTimes, 0.99);
        row.ReferencesMax = referenceTimes.Count == 0 ? 0 : referenceTimes[^1];
        row.RenameP99 = Percentile(renameTimes, 0.99);
        row.RenameMax = renameTimes.Count == 0 ? 0 : renameTimes[^1];
        row.HandlerFiles = lensTimes.Count;
    }

    /// <summary>Runs once to warm, then times a second call, as the other request timings do.</summary>
    private static async Task<double> TimeTwiceAsync<T>(Func<Task<T>> request)
    {
        await request();

        long started = Stopwatch.GetTimestamp();
        await request();
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    /// <summary>GSC and CSC only — headers are inserted, not navigated from.</summary>
    private static List<string> ScriptFilesOnly(List<string> paths)
    {
        List<string> scripts = [];
        foreach ( string path in paths )
        {
            if ( ScriptAnalysis.LanguageFromPath(path) is ScriptLanguage.Gsc or ScriptLanguage.Csc )
            {
                scripts.Add(path);
            }
        }

        return scripts;
    }
}
