using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Server.Configuration;
using GSCode.Server.Formatting;
using GSCode.Server.Handlers;
using GSCode.Workspace.Api;
using GSCode.Workspace.Completion;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using Newtonsoft.Json;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using LspPosition = OmniSharp.Extensions.LanguageServer.Protocol.Models.Position;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using SymbolKind = GSCode.Core.Symbols.SymbolKind;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// Every request the editor can make, timed through its handler, wired the way
/// <c>ServerServices</c> wires the server — <see cref="NavigationSupport"/> WITH the builtin library
/// (without it a builtin call's references take a different path than in the editor), one
/// <see cref="InsertCache"/> shared by the document store and every insert provider.
///
/// The other partials time the rows PERF.md's scale table budgets; this one is the full surface:
/// per-document requests (symbols, semantic tokens, folding, selection, links, formatting, inlay
/// hints, CodeLens, code actions), per-position requests at a stock call, a call into the file's own
/// namespace and a declaration (hover, definition, type definition, implementation, signature help,
/// completion, prepare-rename, rename, references, highlight, call hierarchy), the KEYSTROKE BUNDLE —
/// what one edit costs end to end, re-analysis, the lint pass and the five requests the editor
/// re-issues after it, against the 250 ms debounce — and the watched-file paths: a header every file
/// inserts, a stock script every file uses, a file created and deleted.
///
/// Every request runs once untimed, the JIT is let settle, and a second pass is timed — the same
/// method the request rows use, for the same reason (see <see cref="MeasureRequests"/>). The timed
/// pass also serializes each answer, timed apart, since a large list costs more on the wire than to
/// build (PERF.md, literal completion).
/// </summary>
public partial class ScalePerfTests
{
    /// <summary>Copies sampled per source script; one source is one shape of file.</summary>
    private const int ComponentCopiesPerSource = 2;

    /// <summary>Copies sampled on the mixed shape, which has no sources.</summary>
    private const int ComponentMixedSample = 40;

    /// <summary>One request kind's distribution across the sampled files, and what it sent.</summary>
    private sealed class RequestStats
    {
        public List<double> Milliseconds { get; } = [];
        public List<double> SerializeMilliseconds { get; } = [];
        public List<long> PayloadChars { get; } = [];
        public int Empty { get; set; }

        /// <summary>
        /// The times of the requests no garbage collection ran during. A slow tail of a request
        /// that does not read the workspace at all, such as formatting, is a collection landing in
        /// it until this says otherwise.
        /// </summary>
        public List<double> Uncollected { get; } = [];
    }

    private sealed class ComponentRow
    {
        public int SampledFiles { get; set; }
        public SortedDictionary<string, RequestStats> Requests { get; } = new(StringComparer.Ordinal);
        public SortedDictionary<string, RequestStats> Keystroke { get; } = new(StringComparer.Ordinal);
        public List<(string Name, string Detail)> Watched { get; } = [];
    }

    /// <summary>The server's handlers over one finished index, wired as <c>ServerServices</c> wires them.</summary>
    private sealed class ScaleServer
    {
        public ScaleServer(
            ScriptDatabase database, PathResolver resolver, NameTable names, InsertCache inserts,
            BuiltinApiSet builtins, ObjectFields objectFields)
        {
            PhysicalFileSystem files = new();
            Holder = new ResolverHolder(files) { Current = resolver };
            Documents = new DocumentStore(
                path =>
                {
                    PathResolver current = Holder.Current;
                    return new ResolverInsertProvider(current, current.GetContext(path), files, inserts);
                },
                names,
                inserts);
            Support = new NavigationSupport(Documents, database, Holder, builtins);
            EngineNames = builtins.EngineNamesFor(ScriptLanguage.Gsc);
            Settings = new ServerSettings { CodeLensEnabled = true, InlayParameterNames = true, InlayInferredTypes = true };
            Linter = new DocumentLinter(database, Holder, builtins, objectFields);
            TextDocumentSelector selector = TextDocumentSelector.ForLanguage("gsc");
            FormattingSupport formatting = new(Documents, Holder, StockScripts.Empty, Settings, Support, builtins);

            Symbols = new DocumentSymbolHandler(Documents, Settings, selector);
            Tokens = new SemanticTokensHandler(Documents, selector);
            Folding = new FoldingRangeHandler(Documents, selector);
            Selection = new SelectionRangeHandler(Documents, selector);
            Links = new DocumentLinkHandler(Support, selector);
            Format = new DocumentFormattingHandler(formatting, selector);
            RangeFormat = new DocumentRangeFormattingHandler(formatting, selector);
            OnTypeFormat = new DocumentOnTypeFormattingHandler(formatting, selector);
            Hints = new InlayHintHandler(Support, builtins, objectFields, Settings, selector);
            LensCounts = new LensCountCache();
            Lenses = new CodeLensHandler(Support, Settings, selector, LensCounts);
            Actions = new CodeActionHandler(Documents, Support, Linter, selector);
            Hover = new HoverHandler(Support, builtins, objectFields, selector);
            Definition = new DefinitionHandler(Support, selector);
            TypeDefinition = new TypeDefinitionHandler(Support, builtins, objectFields, selector);
            Implementation = new ImplementationHandler(Support, selector);
            Signature = new SignatureHelpHandler(Support, new SignatureEngine(database, builtins), selector);
            Completion = new CompletionHandler(Support, new CompletionEngine(database, builtins, objectFields), Settings, selector, builtins);
            PrepareRename = new PrepareRenameHandler(Support, builtins, objectFields, selector);
            Rename = new RenameHandler(Support, builtins, objectFields, selector);
            References = new ReferencesHandler(Support, selector);
            Highlight = new DocumentHighlightHandler(Support, selector);
            Calls = new CallHierarchyHandler(Support, selector);
            Types = new TypeHierarchyHandler(Support, selector);
        }

        public ResolverHolder Holder { get; }
        public DocumentStore Documents { get; }
        public NavigationSupport Support { get; }

        /// <summary>The engine's function names, which an include dialect's foreign call must not be.</summary>
        public FrozenSet<string> EngineNames { get; }
        public ServerSettings Settings { get; }
        public DocumentLinter Linter { get; }
        public DocumentSymbolHandler Symbols { get; }
        public SemanticTokensHandler Tokens { get; }
        public FoldingRangeHandler Folding { get; }
        public SelectionRangeHandler Selection { get; }
        public DocumentLinkHandler Links { get; }
        public DocumentFormattingHandler Format { get; }
        public DocumentRangeFormattingHandler RangeFormat { get; }
        public DocumentOnTypeFormattingHandler OnTypeFormat { get; }
        public InlayHintHandler Hints { get; }
        public CodeLensHandler Lenses { get; }

        public LensCountCache LensCounts { get; }
        public CodeActionHandler Actions { get; }
        public HoverHandler Hover { get; }
        public DefinitionHandler Definition { get; }
        public TypeDefinitionHandler TypeDefinition { get; }
        public ImplementationHandler Implementation { get; }
        public SignatureHelpHandler Signature { get; }
        public CompletionHandler Completion { get; }
        public PrepareRenameHandler PrepareRename { get; }
        public RenameHandler Rename { get; }
        public ReferencesHandler References { get; }
        public DocumentHighlightHandler Highlight { get; }
        public CallHierarchyHandler Calls { get; }
        public TypeHierarchyHandler Types { get; }
    }

    /// <summary>The positions one file's requests are asked at, found from its own parse.</summary>
    /// <param name="DeclarationBody">The first function's whole range, for range formatting.</param>
    /// <param name="DeclarationEnd">Just past the first function's closing brace, where typing <c>}</c> asks for on-type formatting.</param>
    /// <param name="Class">The first class's name, for the type hierarchy; null on a dialect without classes.</param>
    private sealed record Positions(
        LspPosition? StockCall, LspPosition? InsideStockCall, LspPosition? LocalCall, LspPosition? Declaration, LspRange? StockCallRange,
        LspRange? DeclarationBody, LspPosition? DeclarationEnd, LspPosition? Class);

    private async Task<ComponentRow> MeasureEveryComponentAsync(
        ScaleCorpus corpus, ScriptDatabase database, PathResolver resolver, NameTable names,
        BuiltinApiSet builtins, ObjectFields objectFields)
    {
        ComponentRow row = new();
        ScaleServer server = new(database, resolver, names, new InsertCache(), builtins, objectFields);
        List<string> sample = ComponentSample(corpus);
        row.SampledFiles = sample.Count;

        // Pass one untimed, so every method the timed pass reaches is compiled; pass two timed.
        await RunComponentPassAsync(server, sample, row, timed: false);
        WaitForJitToSettle();
        await RunComponentPassAsync(server, sample, row, timed: true);

        return row;
    }

    /// <summary>
    /// Copies of every source, from different thousand-file buckets, and the stock sources
    /// themselves — the files every copy calls into, so where CodeLens and references are heaviest.
    /// </summary>
    private static List<string> ComponentSample(ScaleCorpus corpus)
    {
        List<string> copies = ScriptFilesOnly(ScaleCorpusFixture.CopiedScripts(corpus));
        if ( corpus.Sources.Length == 0 )
        {
            return Sample(copies, ComponentMixedSample);
        }

        List<string> sample = [];
        foreach ( string source in corpus.Sources )
        {
            string stem = Path.GetFileNameWithoutExtension(source) + "_";
            List<string> ofSource = [];
            foreach ( string copy in copies )
            {
                if ( Path.GetFileName(copy).StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetExtension(copy), Path.GetExtension(source), StringComparison.OrdinalIgnoreCase) )
                {
                    ofSource.Add(copy);
                }
            }

            sample.AddRange(Sample(ofSource, ComponentCopiesPerSource));
            sample.Add(source);
        }

        return sample;
    }

    private static async Task RunComponentPassAsync(ScaleServer server, List<string> sample, ComponentRow row, bool timed)
    {
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

            // Each file starts with no counts kept, so its first CodeLens request is what opening it
            // costs and the one after its edit is what typing in it costs. The untimed pass must not
            // leave the timed one counts to find.
            server.LensCounts.Clear();
            OpenDocument document = server.Documents.Open(path, text, version: 1);
            ParseResult parsed = server.Documents.AnalyzeIfStale(document);
            TextDocumentIdentifier id = new() { Uri = DocumentUri.FromFileSystemPath(path) };
            Positions at = PositionsIn(parsed, text, server.EngineNames);
            LspRange whole = new(new LspPosition(0, 0), new LspPosition(CountLines(text), 0));

            IDictionary<string, RequestStats> into = timed ? row.Requests : new SortedDictionary<string, RequestStats>();

            await TimeAsync(into, "document symbols", () => server.Symbols.Handle(new DocumentSymbolParams { TextDocument = id }, CancellationToken.None));
            await TimeAsync(into, "semantic tokens", () => server.Tokens.Handle(new SemanticTokensParams { TextDocument = id }, CancellationToken.None));
            await TimeAsync(into, "folding ranges", () => server.Folding.Handle(new FoldingRangeRequestParam { TextDocument = id }, CancellationToken.None));
            await TimeAsync(into, "document links", () => server.Links.Handle(new DocumentLinkParams { TextDocument = id }, CancellationToken.None));
            await TimeAsync(into, "formatting, whole file", () => server.Format.Handle(
                new DocumentFormattingParams { TextDocument = id, Options = new FormattingOptions { TabSize = 4, InsertSpaces = false } },
                CancellationToken.None));
            await TimeAsync(into, "inlay hints, whole file", () => server.Hints.Handle(new InlayHintParams { TextDocument = id, Range = whole }, CancellationToken.None));
            await TimeAsync(into, "codeLens", () => FirstScreenLensesAsync(server, id));
            server.LensCounts.Clear();
            await TimeAsync(into, "codeLens, every lens resolved", () => server.Lenses.HandleResolvedAsync(
                new CodeLensParams { TextDocument = id }, CancellationToken.None));
            server.LensCounts.Clear();

            if ( at.StockCall is LspPosition call && at.StockCallRange is LspRange callRange )
            {
                await TimeAsync(into, "selection range", () => server.Selection.Handle(
                    new SelectionRangeParams { TextDocument = id, Positions = new Container<LspPosition>(call) }, CancellationToken.None));
                await TimeAsync(into, "code actions", () => server.Actions.Handle(
                    new CodeActionParams { TextDocument = id, Range = callRange, Context = new CodeActionContext { Diagnostics = new Container<Diagnostic>() } },
                    CancellationToken.None));
                await TimeAsync(into, "hover, stock call", () => server.Hover.Handle(new HoverParams { TextDocument = id, Position = call }, CancellationToken.None));
                await TimeAsync(into, "definition, stock call", () => server.Definition.Handle(new DefinitionParams { TextDocument = id, Position = call }, CancellationToken.None));
                await TimeAsync(into, "type definition, stock call", () => server.TypeDefinition.Handle(new TypeDefinitionParams { TextDocument = id, Position = call }, CancellationToken.None));
                await TimeAsync(into, "implementation, stock call", () => server.Implementation.Handle(new ImplementationParams { TextDocument = id, Position = call }, CancellationToken.None));
                await TimeAsync(into, "highlight, stock call", () => server.Highlight.Handle(new DocumentHighlightParams { TextDocument = id, Position = call }, CancellationToken.None));
                await TimeAsync(into, "completion, stock call", () => server.Completion.Handle(new CompletionParams { TextDocument = id, Position = call }, CancellationToken.None));
            }

            if ( at.InsideStockCall is LspPosition inside )
            {
                await TimeAsync(into, "signature help", () => server.Signature.Handle(new SignatureHelpParams { TextDocument = id, Position = inside }, CancellationToken.None));
            }

            if ( at.LocalCall is LspPosition local )
            {
                await TimeAsync(into, "hover, own call", () => server.Hover.Handle(new HoverParams { TextDocument = id, Position = local }, CancellationToken.None));
                await TimeAsync(into, "definition, own call", () => server.Definition.Handle(new DefinitionParams { TextDocument = id, Position = local }, CancellationToken.None));
                await TimeAsync(into, "highlight, own call", () => server.Highlight.Handle(new DocumentHighlightParams { TextDocument = id, Position = local }, CancellationToken.None));
                await TimeAsync(into, "references, own call", () => server.References.Handle(
                    new ReferenceParams { TextDocument = id, Position = local, Context = new ReferenceContext { IncludeDeclaration = true } },
                    CancellationToken.None));
                await TimeAsync(into, "rename, own call", () => server.Rename.Handle(
                    new RenameParams { TextDocument = id, Position = local, NewName = "scale_renamed" }, CancellationToken.None));
            }

            if ( at.DeclarationBody is LspRange body && at.DeclarationEnd is LspPosition end )
            {
                FormattingOptions options = new() { TabSize = 4, InsertSpaces = false };
                await TimeAsync(into, "formatting, one function's range", () => server.RangeFormat.Handle(
                    new DocumentRangeFormattingParams { TextDocument = id, Range = body, Options = options }, CancellationToken.None));
                await TimeAsync(into, "formatting, on typing }", () => server.OnTypeFormat.Handle(
                    new DocumentOnTypeFormattingParams { TextDocument = id, Position = end, Character = "}", Options = options },
                    CancellationToken.None));
            }

            if ( at.Class is LspPosition classAt )
            {
                Container<TypeHierarchyItem>? types = await server.Types.Handle(
                    new TypeHierarchyPrepareParams { TextDocument = id, Position = classAt }, CancellationToken.None);
                await TimeAsync(into, "type hierarchy, prepare", () => server.Types.Handle(
                    new TypeHierarchyPrepareParams { TextDocument = id, Position = classAt }, CancellationToken.None));

                TypeHierarchyItem? type = types?.FirstOrDefault();
                if ( type is not null )
                {
                    await TimeAsync(into, "type hierarchy, supertypes", () => server.Types.Handle(
                        new TypeHierarchySupertypesParams { Item = type }, CancellationToken.None));
                    await TimeAsync(into, "type hierarchy, subtypes", () => server.Types.Handle(
                        new TypeHierarchySubtypesParams { Item = type }, CancellationToken.None));
                }
            }

            if ( at.Declaration is LspPosition declaration )
            {
                await TimeAsync(into, "prepare rename, declaration", () => server.PrepareRename.Handle(
                    new PrepareRenameParams { TextDocument = id, Position = declaration }, CancellationToken.None));

                Container<CallHierarchyItem>? items = await server.Calls.Handle(
                    new CallHierarchyPrepareParams { TextDocument = id, Position = declaration }, CancellationToken.None);
                await TimeAsync(into, "call hierarchy, prepare", () => server.Calls.Handle(
                    new CallHierarchyPrepareParams { TextDocument = id, Position = declaration }, CancellationToken.None));

                CallHierarchyItem? item = items?.FirstOrDefault();
                if ( item is not null )
                {
                    await TimeAsync(into, "call hierarchy, incoming", () => server.Calls.Handle(new CallHierarchyIncomingCallsParams { Item = item }, CancellationToken.None));
                    await TimeAsync(into, "call hierarchy, outgoing", () => server.Calls.Handle(new CallHierarchyOutgoingCallsParams { Item = item }, CancellationToken.None));
                }
            }

            await MeasureKeystrokeAsync(
                server, document, id, text, timed ? row.Keystroke : new SortedDictionary<string, RequestStats>());
            server.Documents.Close(path);
        }
    }

    /// <summary>
    /// One edit, end to end: the buffer changes, the file is re-analysed and linted, and the
    /// editor re-asks for the five things that move with it. The sum is what the debounce has to
    /// cover; each part alone can be under budget while the whole is not.
    /// </summary>
    private static async Task MeasureKeystrokeAsync(
        ScaleServer server, OpenDocument document, TextDocumentIdentifier id, string text, IDictionary<string, RequestStats> into)
    {
        // A comment appended at the end: every position the requests above used stays where it was.
        server.Documents.ApplyChange(document, null, text + "\n// edited\n", document.Version + 1);

        int collections = GC.CollectionCount(0);
        long started = Stopwatch.GetTimestamp();
        ParseResult parsed = Step(into, "1 re-analyse", () => server.Documents.AnalyzeIfStale(document));
        Step(into, "2 lint pass", () => server.Linter.Analyze(document, parsed));
        await TimeAsync(into, "3 semantic tokens", () => server.Tokens.Handle(new SemanticTokensParams { TextDocument = id }, CancellationToken.None));
        await TimeAsync(into, "4 codeLens", () => FirstScreenLensesAsync(server, id));
        await TimeAsync(into, "5 inlay hints", () => server.Hints.Handle(
            new InlayHintParams { TextDocument = id, Range = new LspRange(new LspPosition(0, 0), new LspPosition(CountLines(text) + 2, 0)) },
            CancellationToken.None));
        await TimeAsync(into, "6 document symbols", () => server.Symbols.Handle(new DocumentSymbolParams { TextDocument = id }, CancellationToken.None));
        await TimeAsync(into, "7 folding ranges", () => server.Folding.Handle(new FoldingRangeRequestParam { TextDocument = id }, CancellationToken.None));
        Record(
            into, "= one edit, all of the above", Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            GC.CollectionCount(0) != collections);
    }

    private static RequestStats StatsFor(IDictionary<string, RequestStats> into, string name)
    {
        if ( !into.TryGetValue(name, out RequestStats? stats) )
        {
            stats = new RequestStats();
            into[name] = stats;
        }

        return stats;
    }

    private static void Record(IDictionary<string, RequestStats> into, string name, double milliseconds, bool collected)
    {
        RequestStats stats = StatsFor(into, name);
        stats.Milliseconds.Add(milliseconds);
        if ( !collected )
        {
            stats.Uncollected.Add(milliseconds);
        }
    }

    private static T Step<T>(IDictionary<string, RequestStats> into, string name, Func<T> step)
    {
        int collections = GC.CollectionCount(0);
        long started = Stopwatch.GetTimestamp();
        T result = step();
        Record(into, name, Stopwatch.GetElapsedTime(started).TotalMilliseconds, GC.CollectionCount(0) != collections);
        return result;
    }

    /// <summary>Times one request, then serializes its answer apart from the timing.</summary>
    private static async Task TimeAsync<T>(IDictionary<string, RequestStats> into, string name, Func<Task<T>> request)
    {
        int collections = GC.CollectionCount(0);
        long started = Stopwatch.GetTimestamp();
        T result = await request();
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        Record(into, name, elapsed, GC.CollectionCount(0) != collections);
        RequestStats stats = StatsFor(into, name);

        if ( result is null )
        {
            stats.Empty++;
            return;
        }

        long serializeStarted = Stopwatch.GetTimestamp();
        string payload = JsonConvert.SerializeObject(result);
        stats.SerializeMilliseconds.Add(Stopwatch.GetElapsedTime(serializeStarted).TotalMilliseconds);
        stats.PayloadChars.Add(payload.Length);
    }

    /// <summary>Lines an editor shows at once, whose lenses it resolves on opening a file.</summary>
    private const int ScreenLines = 60;

    /// <summary>
    /// What an editor asks for CodeLens on showing a file: the list, then a resolve for each lens on
    /// the screen. The list carries no counts; each resolve counts one.
    /// </summary>
    private static async Task<CodeLensContainer?> FirstScreenLensesAsync(ScaleServer server, TextDocumentIdentifier id)
    {
        CodeLensContainer? listed = await server.Lenses.Handle(new CodeLensParams { TextDocument = id }, CancellationToken.None);
        if ( listed is null )
        {
            return null;
        }

        List<CodeLens> shown = [];
        foreach ( CodeLens lens in listed )
        {
            shown.Add(lens.Range.Start.Line < ScreenLines ? await server.Lenses.Handle(lens, CancellationToken.None) : lens);
        }

        return new CodeLensContainer(shown);
    }

    private static int CountLines(string text)
    {
        int lines = 0;
        foreach ( char character in text )
        {
            if ( character == '\n' )
            {
                lines++;
            }
        }

        return lines + 1;
    }

    /// <summary>
    /// A call to a script function declared elsewhere (the stock call every copy makes most), the inside of
    /// its argument list, an unqualified call to a function this file declares, and the first
    /// declaration's name.
    /// </summary>
    private static Positions PositionsIn(ParseResult parsed, string text, FrozenSet<string> engineNames)
    {
        ReferenceEntry? stock = HottestForeignCall(parsed, engineNames, out _);
        LspPosition? stockAt = null;
        LspPosition? inside = null;
        LspRange? stockRange = null;
        if ( stock is ReferenceEntry entry )
        {
            stockAt = new LspPosition(entry.Range.Start.Line, entry.Range.Start.Character);
            stockRange = new LspRange(stockAt, new LspPosition(entry.Range.End.Line, entry.Range.End.Character));

            // Just past the opening parenthesis, where signature help is asked for.
            string[] lines = text.Split('\n');
            if ( entry.Range.End.Line < lines.Length )
            {
                string line = lines[entry.Range.End.Line];
                int paren = line.IndexOf('(', Math.Min(entry.Range.End.Character, line.Length));
                if ( paren >= 0 )
                {
                    inside = new LspPosition(entry.Range.End.Line, paren + 1);
                }
            }
        }

        HashSet<string> declared = new(StringComparer.OrdinalIgnoreCase);
        foreach ( FunctionSymbol function in parsed.Extraction.Functions )
        {
            declared.Add(function.KeyName);
        }

        LspPosition? local = null;
        foreach ( ReferenceEntry reference in parsed.Extraction.References )
        {
            if ( reference.Kind == ReferenceKind.Call && !reference.FromMacro && declared.Contains(reference.Key.Name) )
            {
                local = new LspPosition(reference.Range.Start.Line, reference.Range.Start.Character);
                break;
            }
        }

        LspPosition? declaration = null;
        LspRange? body = null;
        LspPosition? bodyEnd = null;
        if ( parsed.Extraction.Functions.Length > 0 )
        {
            FunctionSymbol first = parsed.Extraction.Functions[0];
            declaration = new LspPosition(first.NameRange.Start.Line, first.NameRange.Start.Character);
            bodyEnd = new LspPosition(first.FullRange.End.Line, first.FullRange.End.Character);
            body = new LspRange(new LspPosition(first.FullRange.Start.Line, 0), bodyEnd);
        }

        LspPosition? classAt = null;
        if ( parsed.Extraction.Classes.Length > 0 )
        {
            ClassSymbol firstClass = parsed.Extraction.Classes[0];
            classAt = new LspPosition(firstClass.NameRange.Start.Line, firstClass.NameRange.Start.Character);
        }

        return new Positions(stockAt, inside, local, declaration, stockRange, body, bodyEnd, classAt);
    }

    /// <summary>
    /// The watched-file paths, on the finished index. Run after everything else that reads it: the
    /// header change re-indexes files and the create/delete pair adds and removes one.
    /// </summary>
    private static void MeasureWatchedFiles(
        ScaleCorpus corpus, ScriptDatabase database, WorkspaceIndexer indexer, PathResolver resolver,
        NameTable names, ComponentRow row, double sweepSeconds, bool saveHeader)
    {
        WatchedFileUpdater updater = new(database, indexer);

        // A header every BO3 script inserts. Saving it re-indexes every file that does —
        // WatchedFileUpdater.ReindexInserters — timed through the updater as the server runs it.
        //
        // Only in GSCODE_SCALE_SAVE_ONLY runs. It re-indexes the whole workspace, so in a full run it
        // either has to come last — before the cache and warm-start measurements, which need the
        // index gone — or risk the heap limit with everything the run has built up still around and
        // lose every row after it. It is measured on its own, right after the cold index, instead.
        string header = PathUtil.NormalizeAbsolute(Path.Combine(corpus.RawRoot, @"scripts\shared\shared.gsh"));
        if ( saveHeader && File.Exists(header) )
        {
            // Counted in a call of its own, so the list of every inserting record is not in scope
            // while the save replaces them — a precaution, so the harness holds no more than the
            // server does.
            int inserters = CountInserters(database, header, out double findMilliseconds);

            using HeapSampler sampler = new();
            int gen2Before = GC.CollectionCount(2);
            long started = Stopwatch.GetTimestamp();
            int touched;
            try
            {
                touched = updater.Apply(header, WatchedFileChange.Changed).Count;
            }
            finally
            {
                // Kept even when the save throws: the heap through it is what explains a failure.
                foreach ( string sample in sampler.Stop() )
                {
                    row.Watched.Add(("save shared.gsh, heap", sample));
                }
            }

            double saveSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            row.Watched.Add((
                "save shared.gsh",
                $"{inserters:N0} files insert it (found in {findMilliseconds:F1} ms); the save re-indexed {touched:N0} in {saveSeconds:F1} s, "
                + $"{GC.CollectionCount(2) - gen2Before} gen2 collections"));
        }

        // A stock script every heavy BO3 file #using's. In full mode its closed dependents are
        // re-linted after it changes; how many there are is the size of that.
        string util = PathUtil.NormalizeAbsolute(Path.Combine(corpus.RawRoot, @"scripts\shared\util_shared.gsc"));
        if ( database.Gsc.TryGet(util, out ScriptRecord utilRecord) )
        {
            DocumentStore none = new(_ => new ResolverInsertProvider(resolver, resolver.GetContext(util), new PhysicalFileSystem(), new InsertCache()), names);
            long started = Stopwatch.GetTimestamp();
            HashSet<string> dependents = DependentDiagnosticsRefresher.ClosedDependentsOf(utilRecord, database.Gsc, none);
            double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            int total = database.Gsc.AllRecords.Count() + database.Csc.AllRecords.Count();
            row.Watched.Add((
                "change util_shared.gsc",
                $"{dependents.Count:N0} closed dependents of {total:N0} scripts (found in {milliseconds:F1} ms); full mode re-lints each, "
                + $"~{sweepSeconds * dependents.Count / Math.Max(1, total):F1} s as that share of the measured sweep"));
        }

        // A new file and its deletion: the resolution cache is cleared on both, wholesale.
        List<string> copies = ScaleCorpusFixture.CopiedScripts(corpus);
        if ( copies.Count > 0 )
        {
            string created = Path.Combine(Path.GetDirectoryName(copies[0])!, "scale_created_probe.gsc");
            File.WriteAllText(created, "#namespace scale_created_probe;\nfunction probe()\n{\n}\n");
            try
            {
                long started = Stopwatch.GetTimestamp();
                updater.Apply(created, WatchedFileChange.Created);
                double createMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                File.Delete(created);
                started = Stopwatch.GetTimestamp();
                updater.Apply(created, WatchedFileChange.Deleted);
                double deleteMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                row.Watched.Add(("create, then delete, one file", $"{createMilliseconds:F1} ms / {deleteMilliseconds:F1} ms"));
            }
            finally
            {
                if ( File.Exists(created) )
                {
                    File.Delete(created);
                }
            }
        }
    }

    /// <summary>How many records insert a header, and how long finding them took; the list itself is not kept.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int CountInserters(ScriptDatabase database, string header, out double findMilliseconds)
    {
        long started = Stopwatch.GetTimestamp();
        int count = DatabaseQueries.ScriptsInserting(database, header, @"scripts\shared\shared.gsh").Count;
        findMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return count;
    }

    private void WriteComponents(ComponentRow components)
    {
        _output.WriteLine($"     every request, {components.SampledFiles} files (p50 / p99 / max ms; payload max)");
        foreach ( KeyValuePair<string, RequestStats> request in components.Requests )
        {
            _output.WriteLine("       " + DescribeRequest(request.Key, request.Value));
        }

        _output.WriteLine("     one edit, end to end (p50 / p99 / max ms)");
        foreach ( KeyValuePair<string, RequestStats> step in components.Keystroke )
        {
            _output.WriteLine("       " + DescribeRequest(step.Key, step.Value));
        }

        foreach ( (string Name, string Detail) watched in components.Watched )
        {
            _output.WriteLine($"     watched: {watched.Name,-30} {watched.Detail}");
        }
    }

    private static string DescribeRequest(string name, RequestStats stats)
    {
        List<double> times = [.. stats.Milliseconds];
        times.Sort();
        long payload = stats.PayloadChars.Count == 0 ? 0 : stats.PayloadChars.Max();
        double serialize = stats.SerializeMilliseconds.Count == 0 ? 0 : stats.SerializeMilliseconds.Max();
        string empty = stats.Empty > 0 ? $", {stats.Empty} empty" : "";
        int collected = stats.Milliseconds.Count - stats.Uncollected.Count;
        string collections = collected > 0
            ? $", {collected} hit a GC, max of the rest {(stats.Uncollected.Count == 0 ? 0 : stats.Uncollected.Max()):F2}"
            : "";
        return $"{name,-32} {Percentile(times, 0.5),8:F2} {Percentile(times, 0.99),8:F2} {(times.Count == 0 ? 0 : times[^1]),8:F2}"
            + $"   {payload / 1024.0,8:F1} KB, serialize max {serialize:F1} ms ({times.Count} requests{empty}{collections})";
    }

    /// <summary>The every-request table for the report, one column per size, p99.</summary>
    private static void AppendComponentTable(StringBuilder table, List<ScaleRow> rows)
    {
        List<ScaleRow> measured = [.. rows.Where(row => row.Components is not null)];
        if ( measured.Count == 0 )
        {
            return;
        }

        SortedSet<string> names = new(StringComparer.Ordinal);
        foreach ( ScaleRow row in measured )
        {
            foreach ( string name in row.Components!.Requests.Keys )
            {
                names.Add(name);
            }
        }

        table.AppendLine();
        table.Append("| request, p99 ms (max payload KB) |");
        foreach ( ScaleRow row in measured )
        {
            table.Append($" {row.Game} {row.Total:N0} |");
        }

        table.AppendLine();
        table.Append("|---|");
        foreach ( ScaleRow _ in measured )
        {
            table.Append("---:|");
        }

        table.AppendLine();
        foreach ( string name in names )
        {
            table.Append($"| {name} |");
            foreach ( ScaleRow row in measured )
            {
                if ( row.Components!.Requests.TryGetValue(name, out RequestStats? stats) )
                {
                    List<double> times = [.. stats.Milliseconds];
                    times.Sort();
                    long payload = stats.PayloadChars.Count == 0 ? 0 : stats.PayloadChars.Max();
                    table.Append($" {Percentile(times, 0.99):F2} ({payload / 1024.0:F0}) |");
                }
                else
                {
                    table.Append(" — |");
                }
            }

            table.AppendLine();
        }

        table.AppendLine();
        table.Append("| one edit, p99 ms |");
        foreach ( ScaleRow row in measured )
        {
            table.Append($" {row.Game} {row.Total:N0} |");
        }

        table.AppendLine();
        table.Append("|---|");
        foreach ( ScaleRow _ in measured )
        {
            table.Append("---:|");
        }

        table.AppendLine();
        SortedSet<string> steps = new(StringComparer.Ordinal);
        foreach ( ScaleRow row in measured )
        {
            foreach ( string step in row.Components!.Keystroke.Keys )
            {
                steps.Add(step);
            }
        }

        foreach ( string step in steps )
        {
            table.Append($"| {step} |");
            foreach ( ScaleRow row in measured )
            {
                if ( row.Components!.Keystroke.TryGetValue(step, out RequestStats? stats) )
                {
                    List<double> times = [.. stats.Milliseconds];
                    times.Sort();
                    table.Append($" {Percentile(times, 0.99):F1} |");
                }
                else
                {
                    table.Append(" — |");
                }
            }

            table.AppendLine();
        }

        foreach ( ScaleRow row in measured )
        {
            foreach ( (string Name, string Detail) watched in row.Components!.Watched )
            {
                table.AppendLine($"- {row.Game} {row.Total:N0}, {watched.Name}: {watched.Detail}");
            }
        }
    }
}
