using System.Collections.Concurrent;
using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;

namespace GSCode.Workspace.Documents;

/// <summary>
/// A completed analysis and the document version whose text produced it, published as one
/// immutable pair.
///
/// The two were separate fields, written one after the other. Two analyses can run on one document
/// at once — the debounced one on a thread-pool continuation while a request thread enters
/// <see cref="DocumentStore.AnalyzeIfStale"/> — so the writes could interleave into a NEW version
/// stamped on an OLD parse: a document that reports itself fresh while holding text the user has
/// already replaced, which is exactly what the staleness check exists to prevent. One reference
/// write of a pair cannot come apart that way.
/// </summary>
/// <param name="HeaderGeneration">
/// The <see cref="IHeaderMacroCache.Generation"/> the headers in this parse were read at, taken
/// BEFORE the analysis rather than after. A header edit landing mid-parse must leave the result
/// looking old, not be stamped as already seen — the conservative direction costs one extra parse
/// and the other loses the edit until the next keystroke.
/// </param>
public sealed record AnalysisSnapshot(ParseResult Result, int Version, long HeaderGeneration);

/// <summary>One open editor document: its live text and latest analysis.</summary>
public sealed class OpenDocument
{
    public required string Path { get; init; }
    public required ScriptLanguage Language { get; init; }
    public required SourceText Text { get; set; }
    public int Version { get; set; }

    private AnalysisSnapshot? _analysis;

    /// <summary>
    /// The latest published analysis and the version it came from; null before the first run
    /// finishes.
    ///
    /// Text updates synchronously on every keystroke while analysis is debounced, so between an
    /// edit and the debounce firing the snapshot's version and <see cref="Version"/> disagree. Any
    /// feature that pairs a LIVE cursor position with the STALE result text is then reading the
    /// wrong characters entirely.
    /// </summary>
    public AnalysisSnapshot? Analysis
    {
        get { return Volatile.Read(ref _analysis); }
    }

    /// <summary>Latest completed analysis; null only before the first run finishes.</summary>
    public ParseResult? LatestResult
    {
        get { return Analysis?.Result; }
    }

    /// <summary>The <see cref="Version"/> <see cref="LatestResult"/> was produced from, or -1 before the first run.</summary>
    public int AnalyzedVersion
    {
        get { return Analysis?.Version ?? -1; }
    }

    /// <summary>True when the text has moved on since the last completed analysis.</summary>
    public bool IsStale
    {
        get
        {
            AnalysisSnapshot? analysis = Analysis;
            return analysis is null || analysis.Version != Version;
        }
    }

    /// <summary>
    /// Publishes an analysis, unless one from a newer version is already published, and returns
    /// whichever now stands.
    ///
    /// Analyses do not complete in the order they started: a request-thread run that began first
    /// can return after the debounced run that overtook it. Letting the last writer win would put
    /// a parse of two-edits-ago text back in front of the editor, so the version decides instead.
    /// The caller is handed the winner because it publishes diagnostics from what it gets back,
    /// and a superseded parse must not be what those describe.
    /// </summary>
    public AnalysisSnapshot Publish(ParseResult result, int version, long headerGeneration = 0)
    {
        AnalysisSnapshot published = new(result, version, headerGeneration);

        while ( true )
        {
            AnalysisSnapshot? current = Volatile.Read(ref _analysis);

            // Newer text wins outright; at the same text, the one that read the newer headers does.
            // Without the second half a re-analysis forced by a header edit would be discarded as
            // "same version", which is the whole reason it was run.
            if ( current is not null
                && (current.Version > version
                    || (current.Version == version && current.HeaderGeneration >= headerGeneration)) )
            {
                return current;
            }

            if ( ReferenceEquals(Interlocked.CompareExchange(ref _analysis, published, current), current) )
            {
                return published;
            }
        }
    }

    /// <summary>Cancels the in-flight debounced analysis when a newer edit arrives.</summary>
    public CancellationTokenSource? PendingAnalysis { get; set; }
}

/// <summary>
/// Tracks open documents and runs their analyses. Text updates are synchronous and
/// cheap; analysis is triggered by the sync handler (debounced for didChange,
/// immediate for open/save). Closed files fall out entirely.
/// </summary>
public sealed class DocumentStore
{
    private readonly ConcurrentDictionary<string, OpenDocument> _documents = new(StringComparer.Ordinal);
    private readonly Func<string, IInsertProvider> _insertProviderFactory;
    private readonly NameTable _names;

    private readonly IHeaderMacroCache? _headerCache;

    public DocumentStore(
        Func<string, IInsertProvider> insertProviderFactory, NameTable names,
        IHeaderMacroCache? headerCache = null)
    {
        _insertProviderFactory = insertProviderFactory;
        _names = names;
        _headerCache = headerCache;
    }

    public OpenDocument Open(string path, string text, int version)
    {
        string normalized = PathUtil.NormalizeAbsolute(path);
        OpenDocument document = new()
        {
            Path = normalized,
            Language = ScriptAnalysis.LanguageFromPath(normalized),
            Text = SourceText.From(text),
            Version = version,
        };

        _documents[normalized] = document;
        return document;
    }

    public bool TryGet(string path, out OpenDocument document)
    {
        return _documents.TryGetValue(PathUtil.NormalizeAbsolute(path), out document!);
    }

    /// <summary>
    /// An open document together with its latest completed analysis, or false when the path is not
    /// open or nothing has finished analysing it yet.
    ///
    /// The pair rather than two steps, because no caller wants one without the other: five handlers
    /// each wrote the lookup and the null check out by hand, which is five spellings of one
    /// question. Reads <see cref="OpenDocument.Analysis"/> once, for the reason that property
    /// exists — a separate <see cref="OpenDocument.LatestResult"/> read can straddle a publish and
    /// hand back a parse from a different version than the one just found.
    ///
    /// This is the CHEAP resolve, deliberately. It answers only what the store knows; anything
    /// needing the database, the resolution context or a fresh parse goes through the server's
    /// navigation support instead.
    /// </summary>
    public bool TryGetAnalyzed(string path, out OpenDocument document, out ParseResult result)
    {
        if ( !TryGet(path, out document) )
        {
            result = null!;
            return false;
        }

        AnalysisSnapshot? analysis = document.Analysis;
        if ( analysis is null )
        {
            result = null!;
            return false;
        }

        result = analysis.Result;
        return true;
    }

    /// <summary>
    /// Every document the user currently has open, for the cross-file lints: a file's diagnostics
    /// depend on its neighbours, so editing one can invalidate another's. A snapshot rather than a
    /// live view, since the caller re-analyses each one and a document may be closed meanwhile.
    /// </summary>
    public ImmutableArray<OpenDocument> OpenDocuments => [.. _documents.Values];

    public void Close(string path)
    {
        if ( _documents.TryRemove(PathUtil.NormalizeAbsolute(path), out OpenDocument? document) )
        {
            document.PendingAnalysis?.Cancel();
        }
    }

    /// <summary>Applies one LSP incremental change (or a full replacement when range is null).</summary>
    public void ApplyChange(OpenDocument document, TextRange? range, string newText, int version)
    {
        if ( range is null )
        {
            document.Text = SourceText.From(newText);
        }
        else
        {
            int start = document.Text.GetOffset(range.Value.Start);
            int end = document.Text.GetOffset(range.Value.End);
            string existing = document.Text.Text;
            document.Text = SourceText.From(string.Concat(existing.AsSpan(0, start), newText, existing.AsSpan(end)));
        }

        document.Version = version;
    }

    /// <summary>Runs the full per-file pipeline on the document's current text.</summary>
    public ParseResult Analyze(OpenDocument document)
    {
        // Read the version and the text TOGETHER, before anything slow, and analyse those: an edit
        // arriving mid-analysis must leave the document marked stale, not stamped with a version
        // whose text was never analysed. Both are read from a document another thread is free to
        // edit, so taking them in one place is what keeps the pair the analysis is stamped with
        // consistent.
        int version = document.Version;
        SourceText text = document.Text;

        // Read with them, and for the same reason: a header edit landing mid-analysis must leave
        // the result looking old rather than be stamped as already seen.
        long headerGeneration = HeaderGeneration;

        ParseResult result = ScriptAnalysis.Analyze(
            document.Path,
            document.Language,
            text,
            _insertProviderFactory(document.Path),
            _names,
            profile: null,
            headerCache: _headerCache);

        return document.Publish(result, version, headerGeneration).Result;
    }

    /// <summary>
    /// Where the headers stand right now, or 0 when nothing caches them (tests, and any store built
    /// without one — a document with no cache has no header that can move behind it).
    /// </summary>
    private long HeaderGeneration
    {
        get { return _headerCache?.Generation ?? 0; }
    }

    /// <summary>
    /// The document's analysis, re-running it first when the text — or a header it inserts — has
    /// moved on.
    ///
    /// For interactive, position-sensitive features — completion, signature help — where the
    /// request carries a live cursor position that only means anything against matching text.
    /// The debounce exists to keep diagnostics off the keystroke path; it must not make the
    /// editor answer questions about text the user has already replaced.
    ///
    /// The header half is the same argument about a different input. A parse expands whatever the
    /// <c>#insert</c>ed headers said at the time, so editing a GSH invalidates every dependent's
    /// parse without touching a character of it. Checking the document's own version alone let
    /// those parses report themselves current forever, which is why a macro's value in a GSC
    /// updated only once something was typed into the GSC.
    /// </summary>
    public ParseResult AnalyzeIfStale(OpenDocument document)
    {
        // One read of the published pair, not a staleness check followed by a separate fetch: the
        // two reads could straddle a concurrent publish and return a result from a version other
        // than the one just found to be current.
        AnalysisSnapshot? analysis = document.Analysis;
        if ( analysis is not null && analysis.Version == document.Version && analysis.HeaderGeneration == HeaderGeneration )
        {
            return analysis.Result;
        }

        return Analyze(document);
    }
}
