using System.Collections.Concurrent;
using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;

namespace GSCode.Workspace.Documents;

/// <summary>One open editor document: its live text and latest analysis.</summary>
public sealed class OpenDocument
{
    public required string Path { get; init; }
    public required ScriptLanguage Language { get; init; }
    public required SourceText Text { get; set; }
    public int Version { get; set; }

    /// <summary>Latest completed analysis; null only before the first run finishes.</summary>
    public ParseResult? LatestResult { get; set; }

    /// <summary>
    /// The <see cref="Version"/> that <see cref="LatestResult"/> was produced from, or -1 before
    /// the first run.
    ///
    /// Text updates synchronously on every keystroke while analysis is debounced, so between an
    /// edit and the debounce firing these disagree. Any feature that pairs a LIVE cursor position
    /// with the STALE result text is then reading the wrong characters entirely.
    /// </summary>
    public int AnalyzedVersion { get; set; } = -1;

    /// <summary>True when the text has moved on since the last completed analysis.</summary>
    public bool IsStale
    {
        get { return AnalyzedVersion != Version; }
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
        // Read the version FIRST: an edit arriving mid-analysis must leave the document marked
        // stale, not stamped with a version whose text was never analysed.
        int version = document.Version;

        ParseResult result = ScriptAnalysis.Analyze(
            document.Path,
            document.Language,
            document.Text,
            _insertProviderFactory(document.Path),
            _names,
            profile: null,
            headerCache: _headerCache);

        document.LatestResult = result;
        document.AnalyzedVersion = version;
        return result;
    }

    /// <summary>
    /// The document's analysis, re-running it first when the text has moved on.
    ///
    /// For interactive, position-sensitive features — completion, signature help — where the
    /// request carries a live cursor position that only means anything against matching text.
    /// The debounce exists to keep diagnostics off the keystroke path; it must not make the
    /// editor answer questions about text the user has already replaced.
    /// </summary>
    public ParseResult AnalyzeIfStale(OpenDocument document)
    {
        if ( !document.IsStale && document.LatestResult is not null )
        {
            return document.LatestResult;
        }

        return Analyze(document);
    }
}
